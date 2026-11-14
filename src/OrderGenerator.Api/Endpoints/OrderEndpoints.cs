using OrderGenerator.Application.UseCases;

namespace OrderGenerator.Api;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderHttp(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/status", (IOrderTransport transport) => Results.Ok(new { connected = transport.Connected }));
        endpoints.MapPost(OrderHttpPolicy.SubmissionPath, SubmitAsync).RequireRateLimiting(
            OrderHttpPolicy.RateLimitPolicy);
        endpoints.MapGet("/orders/{id}", Get);
        endpoints.MapGet("/exposures", GetExposuresAsync);
        endpoints.MapFallbackToFile("index.html");

        return endpoints;
    }

    private static async Task<IResult> GetExposuresAsync(
        IGetExposuresUseCase get,
        HttpResponse response,
        CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var result = await get.ExecuteAsync(new(), cancellationToken);

        return result.IsSuccess
            ? Results.Ok(new
            {
                items = result.Value.Items.Select(item => new { item.Symbol, item.Exposure, item.Limit })
            })
            : Results.Json(new
            {
                code = result.Error.Code,
                error = result.Error.Message
            }, statusCode: 503);
    }

    private static async Task<IResult> SubmitAsync(
        OrderRequest request,
        ISubmitOrderUseCase submit,
        CancellationToken ct)
    {
        var result = await submit.ExecuteAsync(request.ToSubmitOrderInput(OrderHttpPolicy.ConfirmationTimeout), ct);

        return result.ToHttpResult();
    }

    private static IResult Get(string id, IGetOrderUseCase get) => get.Execute(new(id)) is { IsSuccess: true } result
        ? Results.Ok(
            result.Value.ToResponse())
        : Results.NotFound();
}
