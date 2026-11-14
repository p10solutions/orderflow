using OrderGenerator.Application.UseCases;

namespace OrderGenerator.Api;

public static class OrderResponseMapping
{
    public static OrderResponse ToResponse(this SubmitOrderOutput output) => new(
        output.Id,
        output.Status,
        output.OrderId,
        output.ExecId,
        output.Reason,
        output.Diagnostic);

    public static OrderResponse ToResponse(this GetOrderOutput output) => new(
        output.Id,
        output.Status,
        output.OrderId,
        output.ExecId,
        output.Reason,
        output.Diagnostic);
}
