using OrderAccumulator;
using OrderAccumulator.Domain;
using QuickFix.Fields;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class ExecutionReportTests
{
    [Theory]
    [InlineData(true, Side.BUY)]
    [InlineData(false, Side.BUY)]
    [InlineData(true, Side.SELL)]
    [InlineData(false, Side.SELL)]
    public void R2_Reports_represent_acceptance_or_rejection_without_fills(bool accepted, char side)
    {
        var order = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID("client-order"),
            new Symbol("VIIA4"),
            new Side(side),
            new TransactTime(
                DateTime.UtcNow),
            new OrdType(
                OrdType.LIMIT));
        order.Set(new Symbol("VIIA4"));
        order.Set(new OrderQty(123));
        order.Set(new Price(10.01m));
        var reason = accepted ? "Accepted" : "Exposure limit exceeded";
        var decision = new ProcessOrderOutput(
            "order-id",
            "exec-id",
            accepted,
            reason,
            100m,
            accepted ? 1331.23m : 100m);
        var report = decision.ToExecutionReport(order);
        Assert.Equal("client-order", report.ClOrdID.Value);
        Assert.Equal("order-id", report.OrderID.Value);
        Assert.Equal("exec-id", report.ExecID.Value);
        Assert.Equal("VIIA4", report.Symbol.Value);
        Assert.Equal(side, report.Side.Value);
        Assert.Equal(123m, report.OrderQty.Value);
        Assert.Equal(10.01m, report.Price.Value);
        Assert.Equal(accepted ? ExecType.NEW : ExecType.REJECTED, report.ExecType.Value);
        Assert.Equal(accepted ? OrdStatus.NEW : OrdStatus.REJECTED, report.OrdStatus.Value);
        Assert.Equal(0m, report.CumQty.Value);
        Assert.Equal(0m, report.AvgPx.Value);
        Assert.Equal(accepted ? 123m : 0m, report.LeavesQty.Value);
        Assert.Equal(reason, report.Text.Value);
        Assert.Equal(!accepted, report.IsSetField(Tags.OrdRejReason));

        if (!accepted)
        {
            Assert.Equal(99, report.OrdRejReason.Value);
        }
    }
}
