namespace OrderAccumulator.Domain;

/// <summary>Pure financial decision; persistence and transport are owned by adapters.</summary>
public static class RiskPolicy
{
    public const decimal ExposureLimit = 100_000_000m;

    public static Decision Decide(Order order, decimal before)
    {
        var error = order.Validate();
        var after = before;

        if (error is null)
        {
            var notional = order.Price * order.Quantity;
            after += order.Side == OrderSide.Buy ? notional : -notional;

            if (Math.Abs(after) > ExposureLimit)
            {
                error = "Exposure limit exceeded";
            }
        }

        return error is null ? Decision.Accept(before, after) : Decision.Reject(error, before);
    }
}
