using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderAccumulator;
using OrderAccumulator.Domain;
using OrderGenerator;
using QuickFix;
using QuickFix.Fields;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class ReportValidationTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("symbol")]
    [InlineData("side")]
    [InlineData("quantity")]
    [InlineData("price")]
    [InlineData("cumQty")]
    [InlineData("avgPx")]
    [InlineData("leavesQty")]
    [InlineData("missingPrice")]
    [InlineData("execType")]
    [InlineData("missingReason")]
    [InlineData("missingExecType")]
    [InlineData("missingStatus")]
    [InlineData("missingOrderId")]
    [InlineData("missingExecId")]
    [InlineData("missingText")]
    public void R12_Invalid_report_does_not_complete_and_valid_later_report_does(string field)
    {
        using var initiator = new FixInitiator(
            new ConfigurationBuilder().Build(),
            NullLogger<FixInitiator>.Instance,
            NullLoggerFactory.Instance);
        var id = Guid.NewGuid().ToString("D");
        var session = new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR");
        var order = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(id),
            new Symbol("PETR4"),
            new Side(Side.BUY),
            new TransactTime(
                DateTime.UtcNow),
            new OrdType(
                OrdType.LIMIT));
        order.Set(new OrderQty(10));
        order.Set(new Price(1.01m));
        order.Header.SetField(new MsgSeqNum(1));
        initiator.ToApp(order, session);
        var reports = new List<OrderResult>();
        var diagnostics = new List<ProtocolDiagnostic>();
        initiator.ReportReceived += reports.Add;
        initiator.DiagnosticReceived += diagnostics.Add;
        var report = new ProcessOrderOutput(
            "order",
            "exec",
            field is not ("missingReason" or "missingText"),
            "reason",
            0,
            10.1m).ToExecutionReport(
            order);

        switch (field)
        {
            case "status":
                report.Set(new OrdStatus(OrdStatus.REJECTED));
                break;
            case "symbol":
                report.Set(new Symbol("VALE3"));
                break;
            case "side":
                report.Set(new Side(Side.SELL));
                break;
            case "quantity":
                report.Set(new OrderQty(11));
                break;
            case "price":
                report.Set(new Price(1.02m));
                break;
            case "cumQty":
                report.Set(new CumQty(1));
                break;
            case "avgPx":
                report.Set(new AvgPx(1));
                break;
            case "leavesQty":
                report.Set(new LeavesQty(0));
                break;
            case "missingPrice":
                report.RemoveField(Tags.Price);
                break;
            case "execType":
                report.Set(new ExecType(ExecType.TRADE));
                break;
            case "missingReason":
                report.RemoveField(Tags.OrdRejReason);
                break;
            case "missingExecType":
                report.RemoveField(Tags.ExecType);
                break;
            case "missingStatus":
                report.RemoveField(Tags.OrdStatus);
                break;
            case "missingOrderId":
                report.RemoveField(Tags.OrderID);
                break;
            case "missingExecId":
                report.RemoveField(Tags.ExecID);
                break;
            case "missingText":
                report.RemoveField(Tags.Text);
                break;
        }

        initiator.FromApp(report, session);
        Assert.Empty(reports);
        Assert.Equal(id, Assert.Single(diagnostics).Id);
        initiator.FromApp(
            new ProcessOrderOutput(
                "order",
                "exec",
                true,
                "Accepted",
                0,
                10.1m).ToExecutionReport(order),
            session);
        Assert.Equal("Accepted", Assert.Single(reports).Status);
    }

    [Fact]
    public void R12_Report_without_identity_is_not_assigned_to_an_arbitrary_order()
    {
        using var initiator = new FixInitiator(
            new ConfigurationBuilder().Build(),
            NullLogger<FixInitiator>.Instance,
            NullLoggerFactory.Instance);
        var id = Guid.NewGuid().ToString("D");
        var session = new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR");
        var order = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(id),
            new Symbol("PETR4"),
            new Side(Side.BUY),
            new TransactTime(
                DateTime.UtcNow),
            new OrdType(
                OrdType.LIMIT));
        order.Set(new OrderQty(10));
        order.Set(new Price(1.01m));
        initiator.RestoreCorrelation(new Dictionary<string, OrderPayload>
        {
            [id] = new(
            "PETR4",
            "Buy",
            10,
            1.01m)
        });
        var reports = new List<OrderResult>();
        var diagnostics = new List<ProtocolDiagnostic>();
        initiator.ReportReceived += reports.Add;
        initiator.DiagnosticReceived += diagnostics.Add;
        var report = new ProcessOrderOutput(
            "order",
            "exec",
            true,
            "Accepted",
            0,
            10.1m).ToExecutionReport(order);
        report.RemoveField(Tags.ClOrdID);
        initiator.FromApp(report, session);
        Assert.Empty(reports);
        Assert.Empty(diagnostics);
        report.Set(new ClOrdID(id));
        initiator.FromApp(report, session);
        Assert.Equal(id, Assert.Single(reports).Id);
    }

    [Fact]
    public void R13_Unknown_protocol_reference_cannot_diagnose_an_arbitrary_order()
    {
        using var initiator = new FixInitiator(
            new ConfigurationBuilder().Build(),
            NullLogger<FixInitiator>.Instance,
            NullLoggerFactory.Instance);
        var diagnostics = new List<ProtocolDiagnostic>();
        initiator.DiagnosticReceived += diagnostics.Add;
        var order = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(Guid.NewGuid().ToString("D")),
            new Symbol("PETR4"),
            new Side(
                Side.BUY),
            new TransactTime(
                DateTime.UtcNow),
            new OrdType(
                OrdType.LIMIT));
        order.Set(new OrderQty(1));
        order.Set(new Price(1m));
        order.Header.SetField(new MsgSeqNum(1));
        initiator.ToApp(order, new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR"));
        var reject = new QuickFix.FIX44.BusinessMessageReject(new RefMsgType("D"), new BusinessRejectReason(0));
        reject.Set(new BusinessRejectRefID(Guid.NewGuid().ToString("D")));
        reject.Set(new RefSeqNum(1));
        initiator.FromApp(reject, new SessionID("FIX.4.4", "GENERATOR", "ACCUMULATOR"));
        Assert.Empty(diagnostics);
    }
}
