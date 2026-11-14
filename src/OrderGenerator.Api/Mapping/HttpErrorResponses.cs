namespace OrderGenerator.Api;

internal static class HttpErrorResponses
{
    internal static Task WriteAsync(
        HttpContext context,
        int status,
        OrderError error,
        CancellationToken ct = default)
    {
        context.Response.StatusCode = status;

        return context.Response.WriteAsJsonAsync(error, ct);
    }

    internal static Task BodyTooLargeAsync(HttpContext context) => WriteAsync(
        context,
        StatusCodes.Status413PayloadTooLarge,
        new(
            $"Order body exceeds {OrderHttpPolicy.MaxOrderBodyBytes} bytes",
            "RequestBodyTooLarge"));
}
