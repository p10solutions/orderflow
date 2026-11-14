using OrderGenerator.Api.Middleware;

namespace OrderGenerator.Api;

public static class Pipeline
{
    public static WebApplication UseOrderHttp(this WebApplication app)
    {
        app.UseHostFiltering();
        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseMiddleware<SameOriginMiddleware>();
        app.UseMiddleware<HttpErrorResponseMiddleware>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        // Explicit routing makes endpoint metadata available before rate limiting and body reads.
        app.UseRouting();
        app.UseRateLimiter();
        // Acquire a POST permit before allocating the bounded request buffer.
        app.UseMiddleware<OrderBodyLimitMiddleware>();

        return app;
    }
}
