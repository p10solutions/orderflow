using OrderGenerator.Application.UseCases;

namespace OrderGenerator.Api;

public static class OrderRequestMapping
{
    public static SubmitOrderInput ToSubmitOrderInput(this OrderRequest request, TimeSpan confirmationTimeout) => new(
        request.Id,
        request.Symbol,
        request.Side,
        request.Quantity,
        request.Price,
        confirmationTimeout);
}
