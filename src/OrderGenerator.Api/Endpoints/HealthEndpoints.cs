namespace OrderGenerator.Api;

public static class HealthEndpoints
{
    public static void MapOrderHealth(this WebApplication app, Func<bool> dependenciesReady)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet(
            "/health/ready",
            () => dependenciesReady() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
    }
}
