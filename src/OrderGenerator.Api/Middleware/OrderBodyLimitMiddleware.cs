namespace OrderGenerator.Api.Middleware;

internal sealed class OrderBodyLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || context.GetEndpoint() is not RouteEndpoint { RoutePattern.RawText: OrderHttpPolicy.SubmissionPath })
        {
            await next(context);

            return;
        }

        if (context.Request.ContentLength > OrderHttpPolicy.MaxOrderBodyBytes)
        {
            await HttpErrorResponses.BodyTooLargeAsync(context);

            return;
        }

        // Count decoded bytes, including chunked requests, with a bounded allocation.
        var buffer = new byte[OrderHttpPolicy.MaxOrderBodyBytes + 1];
        var length = 0;

        while (length < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted);

            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > OrderHttpPolicy.MaxOrderBodyBytes)
        {
            await HttpErrorResponses.BodyTooLargeAsync(context);

            return;
        }

        var originalBody = context.Request.Body;
        using var body = new MemoryStream(
            buffer,
            0,
            length,
            writable: false);
        context.Request.Body = body;

        try
        {
            await next(context);
        }
        finally
        {
            context.Request.Body = originalBody;
        }
    }
}
