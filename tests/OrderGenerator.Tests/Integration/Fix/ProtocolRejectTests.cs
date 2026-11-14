using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderAccumulator;
using OrderAccumulator.Domain;
using QuickFix;
using QuickFix.Fields;
using QuickFix.Store;
using QuickFix.Transport;
using Xunit;

namespace OrderGenerator.Tests;

[Collection("FIX socket integration")]
public sealed class ProtocolRejectTests
{
    [Theory]
    [InlineData(OrdType.LIMIT, false, true, 44, 1)]
    [InlineData(OrdType.MARKET, false, true, 40, 5)]
    [InlineData(OrdType.MARKET, true, true, 40, 5)]
    [InlineData(OrdType.LIMIT, true, false, 11, 5)]
    public async Task R14_Reject_identifies_invalid_field_and_reason_without_financial_effect(
        char type,
        bool includePrice,
        bool validId,
        int expectedTag,
        int expectedReason)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Fix:Port"] = port.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            }).Build();
        var book = new ExposureBook();
        using var acceptor = new FixAcceptor(
            new InMemoryOrderProcessor(book),
            configuration,
            NullLogger<FixAcceptor>.Instance,
            NullLoggerFactory.Instance);
        var observer = new Observer();
        var dictionary = Path.Combine(AppContext.BaseDirectory, "FIX44.xml").Replace('\\', '/');
        var settings = new SessionSettings(
            new StringReader(
                $"""
            [DEFAULT]
            ConnectionType=initiator
            StartTime=00:00:00
            EndTime=00:00:00
            HeartBtInt=30
            ReconnectInterval=1
            UseDataDictionary=Y
            DataDictionary={dictionary}
            ResetOnLogon=N
            ResetOnLogout=N
            ResetOnDisconnect=N
            SocketConnectHost=127.0.0.1
            SocketConnectPort={port}
            [SESSION]
            BeginString=FIX.4.4
            SenderCompID=GENERATOR
            TargetCompID=ACCUMULATOR
            """));
        using var initiator = new SocketInitiator(
            observer,
            new MemoryStoreFactory(),
            settings,
            NullLoggerFactory.Instance);

        try
        {
            await acceptor.StartAsync(default);
            initiator.Start();
            await observer.LoggedOn.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var order = new QuickFix.FIX44.NewOrderSingle(
                new ClOrdID(
                    validId ? Guid.NewGuid().ToString("D") : "invalid-uuid"),
                new Symbol("PETR4"),
                new Side(
                    Side.BUY),
                new TransactTime(
                    DateTime.UtcNow),
                new OrdType(
                    type));
            order.Set(new OrderQty(1));

            if (includePrice)
            {
                order.Set(new Price(10m));
            }

            if (!Session.SendToTarget(order, new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR")))
            {
                throw new InvalidOperationException("FIX send unavailable");
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reject = await observer.Rejects.Reader.ReadAsync(timeout.Token);
            Assert.Equal("3", reject.Header.GetString(Tags.MsgType));
            Assert.Equal(expectedTag, reject.GetInt(Tags.RefTagID));
            Assert.Equal(expectedReason, reject.GetInt(Tags.SessionRejectReason));
            Assert.Equal("D", reject.GetString(Tags.RefMsgType));
            Assert.Equal(order.Header.GetULong(Tags.MsgSeqNum), reject.GetULong(Tags.RefSeqNum));
            Assert.False(string.IsNullOrWhiteSpace(reject.GetString(Tags.Text)));
            Assert.Equal(0m, book.Exposure("PETR4"));
        }
        finally
        {
            initiator.Stop();
            await acceptor.StopAsync(default);
        }
    }

    private sealed class Observer : IApplication
    {
        public TaskCompletionSource<bool> LoggedOn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<Message> Rejects { get; } = Channel.CreateUnbounded<Message>();

        public void OnCreate(SessionID sessionID)
        {
        }

        public void OnLogon(SessionID sessionID) => LoggedOn.TrySetResult(true);

        public void OnLogout(SessionID sessionID)
        {
        }

        public void ToAdmin(Message message, SessionID sessionID)
        {
        }

        public void FromAdmin(Message message, SessionID sessionID)
        {
            if (message.Header.GetString(Tags.MsgType) == "3")
            {
                Rejects.Writer.TryWrite(message);
            }
        }

        public void ToApp(Message message, SessionID sessionID)
        {
        }

        public void FromApp(Message message, SessionID sessionID)
        {
        }
    }
}
