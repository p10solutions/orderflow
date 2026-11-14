using QuickFix;
using OrderGenerator.Infrastructure.Fix;
using QuickFix.Fields;
using QuickFix.Store;
using QuickFix.Transport;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderGenerator;

public sealed partial class FixInitiator(
    IConfiguration configuration,
    ILogger<FixInitiator> log,
    ILoggerFactory loggerFactory,
    IFixSequenceStore? sequenceStore = null) : IOrderTransport, IHostedService, IApplication, IDisposable
{
    private SocketInitiator? engine;
    private SessionID? session;
    private volatile bool connected;
    private readonly ConcurrentDictionary<string, SentOrderSnapshot> sent = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ulong, string> sequences = new();
    public event Action<OrderResult>? ReportReceived;
    public event Action<ProtocolDiagnostic>? DiagnosticReceived;
    public bool Connected => connected;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (sequence, id) in sequenceStore?.ReadMappings() ?? new Dictionary<ulong, string>())
        {
            sequences[sequence] = id;
        }

        var settings = CreateSettings();
        IMessageStoreFactory store = configuration["Fix:StorePath"] is null ? new MemoryStoreFactory() : new FileStoreFactory(
            settings);
        engine = new SocketInitiator(
            this,
            store,
            settings,
            loggerFactory);
        engine.Start();

        return Task.CompletedTask;
    }

    internal SessionSettings CreateSettings()
    {
        var dictionary = Path.Combine(AppContext.BaseDirectory, "FIX44.xml").Replace('\\', '/');

        return new SessionSettings(
            new StringReader(
                $"""
            [DEFAULT]
            ConnectionType=initiator
            NonStopSession=Y
            HeartBtInt=30
            ReconnectInterval=2
            UseDataDictionary=Y
            DataDictionary={dictionary}
            ResetOnLogon=N
            ResetOnLogout=N
            ResetOnDisconnect=N
            FileStorePath={configuration["Fix:StorePath"] ?? "unused"}
            SocketConnectHost={configuration["Fix:Host"] ?? "127.0.0.1"}
            SocketConnectPort={configuration.GetValue("Fix:Port", 5001)}
            [SESSION]
            BeginString=FIX.4.4
            SenderCompID=GENERATOR
            TargetCompID=ACCUMULATOR
            """));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        engine?.Stop();

        return Task.CompletedTask;
    }

    public void Dispose() => engine?.Dispose();

    public void OnCreate(SessionID sessionID)
    {
        session = sessionID;
        log.LogInformation("FIX created {Session}", sessionID);
    }

    public void OnLogon(SessionID sessionID)
    {
        connected = true;
        log.LogInformation("FIX connected {Session}", sessionID);
    }

    public void OnLogout(SessionID sessionID)
    {
        connected = false;
        log.LogWarning("FIX disconnected {Session}", sessionID);
    }

    public void ToAdmin(Message message, SessionID sessionID)
    {
    }

    public void FromAdmin(Message message, SessionID sessionID)
    {
        if (message.Header.GetString(Tags.MsgType) == "3")
        {
            DiagnoseReject(message, sessionID);
        }
    }

    public void ToApp(Message message, SessionID sessionID)
    {
        if (message.Header.GetString(Tags.MsgType) != "D" || !message.IsSetField(Tags.ClOrdID))
        {
            return;
        }

        RecordSentOrder(message);
    }

    private void RecordSentOrder(Message message)
    {
        var id = message.GetString(Tags.ClOrdID);
        var sequence = message.Header.GetULong(Tags.MsgSeqNum);
        sequenceStore?.Record(sequence, id);
        sequences[sequence] = id;
        sent[id] = message.ToSentOrderSnapshot();

        if (message.Header.IsSetField(Tags.PossDupFlag) && message.Header.GetBoolean(Tags.PossDupFlag))
        {
            log.LogInformation("FIX replay {ClOrdID} sequence {Sequence}", id, sequence);
        }
    }

    public void FromApp(Message message, SessionID sessionID)
    {
        if (message is QuickFix.FIX44.PositionReport position)
        {
            HandlePositionReport(position);

            return;
        }

        if (message.Header.GetString(Tags.MsgType) == "j")
        {
            DiagnoseReject(message, sessionID);

            return;
        }

        if (message is not QuickFix.FIX44.ExecutionReport report)
        {
            throw new UnsupportedMessageType();
        }

        HandleExecutionReport(report, sessionID);
    }

    private void HandleExecutionReport(QuickFix.FIX44.ExecutionReport report, SessionID sessionID)
    {
        if (!report.IsSetField(Tags.ClOrdID))
        {
            log.LogWarning("ExecutionReport without ClOrdID {Session}; correlation unavailable", sessionID);

            return;
        }

        var id = report.ClOrdID.Value;

        if (!sent.TryGetValue(id, out var payload))
        {
            log.LogWarning("Uncorrelated report {ClOrdID} {Session}", id, sessionID);

            return;
        }

        switch (ExecutionReportDecoder.Decode(id, report, payload))
        {
            case ReportDecodingResult.Invalid invalid:
                DiagnosticReceived?.Invoke(new(id, invalid.Diagnostic));
                break;
            case ReportDecodingResult.Valid valid:
                ReportReceived?.Invoke(valid.Result);
                break;
        }
    }

    private void DiagnoseReject(Message message, SessionID sessionID)
    {
        string? id = null;

        if (message.IsSetField(Tags.BusinessRejectRefID))
        {
            var reference = message.GetString(Tags.BusinessRejectRefID);

            if (sent.ContainsKey(reference))
            {
                id = reference;
            }

            if (message.IsSetField(Tags.RefSeqNum) && sequences.TryGetValue(
                message.GetULong(Tags.RefSeqNum),
                out var sequenceId) && sequenceId != reference)
            {
                log.LogWarning("Conflicting protocol references {Session}", sessionID);

                return;
            }
        }
        else if (message.IsSetField(Tags.RefSeqNum))
        {
            sequences.TryGetValue(message.GetULong(Tags.RefSeqNum), out id);
        }

        var detail = message.IsSetField(Tags.Text) ? message.GetString(Tags.Text) : "FIX protocol rejection";

        if (id is not null)
        {
            DiagnosticReceived?.Invoke(new(id, detail));
        }
        else
        {
            log.LogWarning("Uncorrelated protocol rejection {Session}: {Diagnostic}", sessionID, detail);
        }
    }

    public bool Send(string id, OrderPayload order)
    {
        if (session is null)
        {
            return false;
        }

        return Session.SendToTarget(order.ToNewOrderSingle(id), session);
    }

    public void RestoreCorrelation(IReadOnlyDictionary<string, OrderPayload> orders)
    {
        foreach (var (id, order) in orders)
        {
            sent[id] = order.ToSentOrderSnapshot();
        }
    }
}
