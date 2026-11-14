using OrderGenerator.Application.UseCases;

namespace OrderGenerator.Worker.Fix;

public static class FixReportMapping
{
    public static ConfirmOrderInput ToConfirmOrderInput(this OrderResult report) => new(
        report.Id,
        report.Status,
        report.OrderId,
        report.ExecId,
        report.Reason);

    public static IReadOnlyDictionary<string, OrderPayload> ToCorrelation(this GetKnownOrdersOutput output) => output.Orders.ToDictionary(
        order => order.Id,
        order => new OrderPayload(
            order.Symbol,
            order.Side,
            order.Quantity,
            order.Price));
}
