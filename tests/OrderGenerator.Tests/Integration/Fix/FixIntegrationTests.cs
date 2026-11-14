using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OrderAccumulator;
using OrderAccumulator.Domain;
using OrderGenerator;
using QuickFix;
using QuickFix.Fields;
using Xunit;

namespace OrderGenerator.Tests;

[CollectionDefinition("FIX socket integration", DisableParallelization = true)]
public sealed class FixSocketCollection;
[Collection("FIX socket integration")]
public sealed class FixIntegrationTests
{
    private sealed class ReplayLogger : ILogger<FixInitiator>
    {
        public TaskCompletionSource<bool> Replayed
        {
            get; private set;
        } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void PrepareReplay() => Replayed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("FIX replay ", StringComparison.Ordinal))
            {
                Replayed.TrySetResult(true);
            }
        }
    }

    [Fact]
    public async Task R2_R3_R4_R5_Real_fix_session_accepts_rejects_and_deduplicates_without_reapplying_exposure()
    {
        Assert.True(
            File.Exists(Path.Combine(AppContext.BaseDirectory, "FIX44.xml")),
            "Official FIX44 dictionary must be copied to test output");
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Fix:Host"] = "127.0.0.1",
                ["Fix:Port"] = port.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            }).Build();
        var book = new ExposureBook();
        using var acceptor = new FixAcceptor(
            new InMemoryOrderProcessor(book),
            configuration,
            NullLogger<FixAcceptor>.Instance,
            NullLoggerFactory.Instance);
        var replayLog = new ReplayLogger();
        using var initiator = new FixInitiator(configuration, replayLog, NullLoggerFactory.Instance);
        var registry = new TestOrderApplication(initiator, NullLogger<TestOrderApplication>.Instance);
        var reports = Channel.CreateUnbounded<OrderResult>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var diagnostics = Channel.CreateUnbounded<ProtocolDiagnostic>();
        initiator.DiagnosticReceived += diagnostic =>
        {
            registry.Diagnose(diagnostic);
            diagnostics.Writer.TryWrite(diagnostic);
        };
        initiator.ReportReceived += report =>
        {
            registry.Confirm(report);
            reports.Writer.TryWrite(report);
        };
        async Task<OrderResult> NextReport(string id)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await reports.Reader.ReadAsync(deadline.Token);
            Assert.Equal(id, result.Id);

            return result;
        }

        try
        {
            await acceptor.StartAsync(CancellationToken.None);
            await initiator.StartAsync(CancellationToken.None);

            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                while (!initiator.Connected)
                {
                    await Task.Delay(20, deadline.Token);
                }
            }

            Assert.True(initiator.Connected);
            // A wildcard listener would also accept 127.0.0.2; binding specifically
            // to 127.0.0.1 must reject this separate local address.
            using (var differentLocalAddress = new TcpClient())
            {
                await Assert.ThrowsAsync<SocketException>(
                    async () => await differentLocalAddress.ConnectAsync(
                        IPAddress.Parse("127.0.0.2"),
                        port).WaitAsync(
                        TimeSpan.FromSeconds(5)));
            }

            var acceptedRequest = new OrderRequest(
                Guid.NewGuid().ToString("D"),
                "PETR4",
                "Buy",
                99999,
                "999.99");
            var acceptedResponse = await registry.Submit(
                acceptedRequest,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            Assert.Equal(SubmissionStatus.Confirmed, acceptedResponse.Status);
            var accepted = await NextReport(acceptedRequest.Id);
            Assert.Equal("Accepted", accepted.Status);
            Assert.False(string.IsNullOrWhiteSpace(accepted.OrderId));
            Assert.False(string.IsNullOrWhiteSpace(accepted.ExecId));
            Assert.Equal(99_998_000.01m, book.Exposure("PETR4"));
            var acceptedSequence = Session.LookupSession(new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR"))!.NextSenderMsgSeqNum - 1;
            // Protocol violations are rejected before the financial domain, with a correlated diagnostic.
            async Task SendInvalid(
                char type,
                bool includePrice,
                string id)
            {
                var message = new QuickFix.FIX44.NewOrderSingle(
                    new ClOrdID(id),
                    new Symbol("VALE3"),
                    new Side(Side.BUY),
                    new TransactTime(
                        DateTime.UtcNow),
                    new OrdType(
                        type));
                message.Set(new OrderQty(1));

                if (includePrice)
                {
                    message.Set(new Price(10m));
                }

                Assert.True(Session.SendToTarget(message, new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR")));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var diagnostic = await diagnostics.Reader.ReadAsync(deadline.Token);
                Assert.Equal(id, diagnostic.Id);
                Assert.False(string.IsNullOrWhiteSpace(diagnostic.Message));
                Assert.Equal(0m, book.Exposure("VALE3"));
            }

            await SendInvalid(OrdType.MARKET, false, Guid.NewGuid().ToString("D"));
            await SendInvalid(OrdType.MARKET, true, Guid.NewGuid().ToString("D"));
            await SendInvalid(OrdType.LIMIT, false, Guid.NewGuid().ToString("D"));
            await SendInvalid(OrdType.LIMIT, true, "invalid-uuid");
            var businessReject = new QuickFix.FIX44.BusinessMessageReject(
                new RefMsgType("D"),
                new BusinessRejectReason(0));
            businessReject.Set(new BusinessRejectRefID(acceptedRequest.Id));
            businessReject.Set(new Text("Application protocol failure"));
            Assert.True(Session.SendToTarget(businessReject, new SessionID("FIX.4.4", "ACCUMULATOR", "GENERATOR")));

            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var diagnostic = await diagnostics.Reader.ReadAsync(deadline.Token);
                Assert.Equal(acceptedRequest.Id, diagnostic.Id);
                Assert.Equal("Application protocol failure", diagnostic.Message);
            }

            // Reconnect the existing engines, preserving stores and financial state.
            var liveSession = Session.LookupSession(new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR"))!;
            var sequenceBefore = liveSession.NextSenderMsgSeqNum;
            liveSession.Disconnect("Controlled reconnection test");
            Assert.False(initiator.Connected);

            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                while (!initiator.Connected)
                {
                    await Task.Delay(20, deadline.Token);
                }
            }

            Assert.True(initiator.Send(acceptedRequest.Id, new(
                "PETR4",
                "Buy",
                99999,
                999.99m)));
            Assert.Equal(accepted, await NextReport(acceptedRequest.Id));
            Assert.True(liveSession.NextSenderMsgSeqNum > sequenceBefore);
            Assert.Equal(99_998_000.01m, book.Exposure("PETR4"));
            var rejectedRequest = acceptedRequest with
            {
                Id = Guid.NewGuid().ToString("D"),
                Quantity = 3
            };
            Assert.Equal(
                SubmissionStatus.Confirmed,
                (await registry.Submit(
                    rejectedRequest,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None)).Status);
            var rejected = await NextReport(rejectedRequest.Id);
            Assert.Equal("Rejected", rejected.Status);
            Assert.Equal("Exposure limit exceeded", rejected.Reason);
            Assert.Equal(99_998_000.01m, book.Exposure("PETR4"));
            // A new FIX application message with the same business identity is not a transport replay.
            Assert.True(initiator.Send(acceptedRequest.Id, new(
                "PETR4",
                "Buy",
                99999,
                999.9900m)));
            var acceptedDuplicate = await NextReport(acceptedRequest.Id);
            Assert.Equal(accepted, acceptedDuplicate);
            Assert.Equal(99_998_000.01m, book.Exposure("PETR4"));
            Assert.True(initiator.Send(rejectedRequest.Id, new(
                "PETR4",
                "Buy",
                3,
                999.99m)));
            Assert.Equal(rejected, await NextReport(rejectedRequest.Id));
            Assert.True(initiator.Send(acceptedRequest.Id, new(
                "PETR4",
                "Buy",
                99998,
                999.99m)));
            var conflict = await NextReport(acceptedRequest.Id);
            Assert.Equal("Rejected", conflict.Status);
            Assert.Equal("ClOrdID payload conflict", conflict.Reason);
            Assert.NotEqual(accepted.ExecId, conflict.ExecId);
            Assert.Equal(99_998_000.01m, book.Exposure("PETR4"));
            // Exercise fractional quantities on the wire, before the integer domain conversion.
            var fractionalId = Guid.NewGuid().ToString("D");
            void SendFraction(decimal quantity)
            {
                var message = new QuickFix.FIX44.NewOrderSingle(
                    new ClOrdID(fractionalId),
                    new Symbol("VALE3"),
                    new Side(
                        Side.BUY),
                    new TransactTime(
                        DateTime.UtcNow),
                    new OrdType(
                        OrdType.LIMIT));
                message.Set(new OrderQty(quantity));
                message.Set(new Price(10.01m));
                Assert.True(Session.SendToTarget(message, new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR")));
            }

            SendFraction(1.1m);
            var fractional = await NextReport(fractionalId);
            Assert.Equal("Rejected", fractional.Status);
            Assert.Contains("Quantity", fractional.Reason!);
            SendFraction(1.2m);
            var fractionalConflict = await NextReport(fractionalId);
            Assert.Equal("ClOrdID payload conflict", fractionalConflict.Reason);
            Assert.NotEqual(fractional.ExecId, fractionalConflict.ExecId);
            SendFraction(1.10m);
            Assert.Equal(fractional, await NextReport(fractionalId));
            Assert.Equal(0m, book.Exposure("VALE3"));
            Assert.Equal(99_998_000.01m, book.Exposure("PETR4"));
            // Request an actual transport replay from the engine's original message store.
            var resend = new QuickFix.FIX44.ResendRequest(
                new BeginSeqNo(acceptedSequence),
                new EndSeqNo(
                    acceptedSequence));
            replayLog.PrepareReplay();
            Assert.True(Session.SendToTarget(resend, new SessionID("FIX.4.4", "ACCUMULATOR", "GENERATOR")));
            await replayLog.Replayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // A following message forms a wire-order barrier after the replay, whether the peer
            // suppresses the old sequence or passes PossDup through business deduplication.
            Assert.True(initiator.Send(acceptedRequest.Id, new(
                "PETR4",
                "Buy",
                99999,
                999.99m)));
            Assert.Equal(accepted, await NextReport(acceptedRequest.Id));
            Assert.Equal(99_998_000.01m, book.Exposure("PETR4"));
        }
        finally
        {
            await initiator.StopAsync(CancellationToken.None);
            await acceptor.StopAsync(CancellationToken.None);
        }
    }
}
