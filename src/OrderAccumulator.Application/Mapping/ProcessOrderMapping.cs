using OrderAccumulator.Application.UseCases.ProcessOrder;
using OrderAccumulator.Domain;

namespace OrderAccumulator.Application.Mapping;

internal static class ProcessOrderMapping
{
    internal static Order ToDomain(this ProcessOrderInput input) => new(
        input.Symbol,
        (OrderSide)input.Side,
        input.Quantity,
        input.Price);

    internal static ProcessOrderOutput ToOutput(this Decision decision) => new(
        decision.OrderId,
        decision.ExecId,
        decision.Accepted,
        decision.Reason,
        decision.Before,
        decision.After);
}
