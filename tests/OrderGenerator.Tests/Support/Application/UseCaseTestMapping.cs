using OrderAccumulator.Domain;
using OrderGenerator;

namespace OrderGenerator.Tests;

internal static class UseCaseTestMapping
{
    internal static OrderResult ToTestOrderResult(this SubmitOrderOutput output) => new(
        output.Id,
        output.Status,
        output.OrderId,
        output.ExecId,
        output.Reason,
        output.Diagnostic);

    internal static OrderResult ToTestOrderResult(this GetOrderOutput output) => new(
        output.Id,
        output.Status,
        output.OrderId,
        output.ExecId,
        output.Reason,
        output.Diagnostic);

    internal static Decision ToTestDecision(this ProcessOrderOutput output) => new(
        output.OrderId,
        output.ExecId,
        output.Accepted,
        output.Reason,
        output.Before,
        output.After);

    internal static ProcessOrderOutput ToTestOutput(this Decision decision) => new(
        decision.OrderId,
        decision.ExecId,
        decision.Accepted,
        decision.Reason,
        decision.Before,
        decision.After);

    internal static ProcessOrderInput ToTestProcessInput(
        this Order order,
        string session,
        string id,
        string? fingerprint = null) => new(
        session,
        id,
        order.Symbol,
        (int)order.Side,
        order.Quantity,
        order.Price,
        fingerprint);
}
