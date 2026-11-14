using System.Diagnostics;

namespace OrderGenerator.Api.Middleware;

internal sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        await next(context);
        log.LogInformation(
            "HTTP {Method} {Path} status {Status} durationMs {DurationMs}",
            context.Request.Method,
            context.Request.Path,
            context.Response.StatusCode,
            Stopwatch.GetElapsedTime(
                started).TotalMilliseconds);
    }
}
