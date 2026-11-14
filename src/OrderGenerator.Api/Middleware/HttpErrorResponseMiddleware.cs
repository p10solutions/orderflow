namespace OrderGenerator.Api.Middleware;

internal sealed class HttpErrorResponseMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            await HttpErrorResponses.BodyTooLargeAsync(context);
        }

        if (context.Response.HasStarted)
        {
            return;
        }

        if (context.Response.StatusCode == StatusCodes.Status400BadRequest && context.Response.ContentLength is null or 0)
        {
            await HttpErrorResponses.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                new(
                    "Malformed or invalid request body",
                    "ValidationFailed"));
        }
        else if (context.Response.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await HttpErrorResponses.BodyTooLargeAsync(context);
        }
    }
}
