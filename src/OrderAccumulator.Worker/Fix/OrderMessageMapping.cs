using QuickFix.Fields;
using OrderAccumulator.Application.UseCases.ProcessOrder;

namespace OrderAccumulator.Worker.Fix;

public static class OrderMessageMapping
{
    public static ProcessOrderInput ToProcessOrderInput(this QuickFix.FIX44.NewOrderSingle order, string session)
    {
        var quantity = order.OrderQty.Value;
        var integral = quantity == decimal.Truncate(quantity) && quantity is >= 1 and <= 99999;
        // Keep original numeric content in the fingerprint even for invalid financial fields.
        var fingerprint = FormattableString.Invariant(
            $"{order.Symbol.Value}|{order.Side.Value}|{quantity:G29}|{order.Price.Value:G29}|{order.OrdType.Value}");

        return new(
            session,
            order.ClOrdID.Value,
            order.Symbol.Value,
            order.Side.Value - '0',
            integral ? (int)quantity : 0,
            order.Price.Value,
            fingerprint);
    }

    public static QuickFix.FIX44.ExecutionReport ToExecutionReport(
        this ProcessOrderOutput decision,
        QuickFix.FIX44.NewOrderSingle order)
    {
        var report = new QuickFix.FIX44.ExecutionReport();
        report.Set(new OrderID(decision.OrderId));
        report.Set(new ExecID(decision.ExecId));
        report.Set(order.ClOrdID);
        report.Set(order.Symbol);
        report.Set(order.Side);
        report.Set(order.OrderQty);
        report.Set(order.Price);
        report.Set(new ExecType(decision.Accepted ? ExecType.NEW : ExecType.REJECTED));
        report.Set(new OrdStatus(decision.Accepted ? OrdStatus.NEW : OrdStatus.REJECTED));
        report.Set(new LeavesQty(decision.Accepted ? order.OrderQty.Value : 0m));
        report.Set(new CumQty(0m));
        report.Set(new AvgPx(0m));
        report.Set(new Text(decision.Reason));

        if (!decision.Accepted)
        {
            report.Set(new OrdRejReason(99));
        }

        return report;
    }
}
