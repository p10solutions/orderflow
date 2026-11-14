using OrderGenerator.Application.UseCases;
using OrderGenerator.Domain.Orders;
using OrderGenerator.Domain.ValueObjects;

namespace OrderGenerator.Application.Mapping;

internal static class UseCaseContractMapping
{
    internal static GetOrderOutput ToGetOrderOutput(this OrderIntent intent) => new(
        intent.Id.Value,
        intent.Confirmation?.Status.ToString() ?? "PendingConfirmation",
        intent.Confirmation?.OrderId,
        intent.Confirmation?.ExecId,
        intent.Confirmation?.Reason,
        intent.Diagnostic);

    internal static SubmitOrderOutput ToSubmitOrderOutput(this GetOrderOutput output) => new(
        output.Id,
        output.Status,
        output.OrderId,
        output.ExecId,
        output.Reason,
        output.Diagnostic);

    internal static GetKnownOrdersItem ToKnownOrderItem(this OrderIntent intent) => new(
        intent.Id.Value,
        intent.Payload.Symbol,
        intent.Payload.Side,
        intent.Payload.Quantity,
        intent.Payload.Price);

    internal static OrderConfirmation ToConfirmation(this ConfirmOrderInput input) => OrderConfirmation.Create(
        input.Status == "Accepted" ? ConfirmationStatus.Accepted : ConfirmationStatus.Rejected,
        input.OrderId,
        input.ExecId,
        input.Reason);
}
