using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderAccumulator.Application.UseCases.ProcessOrder;
using QuickFix;
using QuickFix.Fields;

namespace OrderAccumulator.Worker.Fix;

public sealed class FixAcceptor(
    IProcessOrderUseCase processOrder,
    IConfiguration configuration,
    ILogger<FixAcceptor> log,
    ILoggerFactory loggerFactory,
    OrderAccumulator.Application.UseCases.GetExposures.IGetExposuresUseCase? getExposures = null) : IHostedService, IApplication, IDisposable
{
    private OrderAccumulator.Infrastructure.FixAcceptorTransport? transport;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        transport = new(this, configuration, loggerFactory);

        return transport.StartAsync(cancellationToken);
    }

    internal SessionSettings CreateSettings() => OrderAccumulator.Infrastructure.FixAcceptorTransport.CreateSettings(
        configuration);

    public Task StopAsync(CancellationToken cancellationToken) => transport?.StopAsync(cancellationToken) ?? Task.CompletedTask;

    public void Dispose() => transport?.Dispose();

    public void OnCreate(SessionID sessionID) => log.LogInformation("FIX created {Session}", sessionID);

    public void OnLogon(SessionID sessionID) => log.LogInformation("FIX connected {Session}", sessionID);

    public void OnLogout(SessionID sessionID) => log.LogWarning("FIX disconnected {Session}", sessionID);

    public void ToAdmin(Message message, SessionID sessionID)
    {
    }

    public void FromAdmin(Message message, SessionID sessionID)
    {
    }

    public void ToApp(Message message, SessionID sessionID)
    {
    }

    public void FromApp(Message message, SessionID sessionID)
    {
        if (message is QuickFix.FIX44.RequestForPositions request)
        {
            if (getExposures is null || request.GetString(Tags.Account) != "ORDERFLOW" ||
                request.GetInt(Tags.PosReqType) != 0 || request.GetChar(Tags.SubscriptionRequestType) != '0')
            {
                throw new UnsupportedMessageType();
            }

            var snapshot = getExposures.Execute(new());
            if (snapshot.IsSuccess)
            {
                foreach (var item in snapshot.Value.Items)
                {
                    Session.SendToTarget(item.ToPositionReport(request.GetString(Tags.PosReqID)), sessionID);
                }
            }

            return;
        }

        if (message is not QuickFix.FIX44.NewOrderSingle order)
        {
            throw new UnsupportedMessageType();
        }

        if (!ValidateProtocol(order, sessionID))
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var input = order.ToProcessOrderInput(sessionID.ToString());
        var result = processOrder.Execute(input);

        if (result.IsFailure)
        {
            log.LogWarning(
                "Invalid order input {ClOrdID}: {Code} {Reason}",
                input.ClOrdId,
                result.Error.Code,
                result.Error.Message);

            return;
        }

        var decision = result.Value;
        log.LogInformation(
            "Order {ClOrdID} session {Session} symbol {Symbol} accepted {Accepted} reason {Reason} exposure {Before} -> {After} ruleMs {RuleMs}",
            order.ClOrdID.Value,
            sessionID,
            input.Symbol,
            decision.Accepted,
            decision.Reason,
            decision.Before,
            decision.After,
            Stopwatch.GetElapsedTime(
                started).TotalMilliseconds);
        SendReport(order, decision, sessionID);
    }

    private static bool ValidateProtocol(QuickFix.FIX44.NewOrderSingle order, SessionID sessionID)
    {
        if (!Guid.TryParseExact(order.ClOrdID.Value, "D", out _))
        {
            RejectProtocol(
                order,
                sessionID,
                Tags.ClOrdID,
                SessionRejectReason.VALUE_IS_INCORRECT,
                "ClOrdID must be a UUID in D format");

            return false;
        }

        if (order.OrdType.Value != OrdType.LIMIT)
        {
            RejectProtocol(
                order,
                sessionID,
                Tags.OrdType,
                SessionRejectReason.VALUE_IS_INCORRECT,
                "Only Limit orders are supported");

            return false;
        }

        if (!order.IsSetField(Tags.Price))
        {
            RejectProtocol(
                order,
                sessionID,
                Tags.Price,
                SessionRejectReason.REQUIRED_TAG_MISSING,
                "Price is required for Limit orders");

            return false;
        }

        return true;
    }

    private void SendReport(
        QuickFix.FIX44.NewOrderSingle order,
        ProcessOrderOutput decision,
        SessionID sessionID)
    {
        var report = decision.ToExecutionReport(order);

        try
        {
            if (!Session.SendToTarget(report, sessionID))
            {
                log.LogWarning("Report send unavailable {ClOrdID} {Session}", order.ClOrdID.Value, sessionID);
            }
        }
        catch (SessionNotFound ex)
        {
            log.LogError(
                ex,
                "Report send failed {ClOrdID} {Session}",
                order.ClOrdID.Value,
                sessionID);
        }
    }

    private static void RejectProtocol(
        Message order,
        SessionID session,
        int tag,
        int rejectReason,
        string reason)
    {
        var reject = new QuickFix.FIX44.Reject(new RefSeqNum(order.Header.GetULong(Tags.MsgSeqNum)));
        reject.Set(new RefTagID(tag));
        reject.Set(new RefMsgType("D"));
        reject.Set(new SessionRejectReason(rejectReason));
        reject.Set(new Text(reason));
        Session.SendToTarget(reject, session);
    }
}
