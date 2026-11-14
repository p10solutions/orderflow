using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace OrderGenerator.Api;

public static class Registration
{
    public static IServiceCollection AddOrderHttp(this IServiceCollection services)
    {
        services.AddHostFiltering(options => options.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"]);
        services.ConfigureHttpJsonOptions(
            options => options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.AddRateLimiter(
            options =>
        {
            options.AddConcurrencyLimiter(
                OrderHttpPolicy.RateLimitPolicy,
                policy =>
            {
                policy.PermitLimit = OrderHttpPolicy.ConcurrentPosts;
                policy.QueueLimit = 0;
                policy.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
            });
            options.OnRejected = (context, ct) => new ValueTask(
                HttpErrorResponses.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status429TooManyRequests,
                    new(
                        "Too many concurrent order requests; retry the same identity",
                        "HttpCapacityReached"),
                    ct));
        });

        return services;
    }
}
