using Microsoft.Extensions.DependencyInjection;
using OrderAccumulator.Worker.Fix;

namespace OrderAccumulator.Worker;

public static class Registration
{
    public static IServiceCollection AddOrderProcessingWorker(this IServiceCollection services)
    {
        services.AddHostedService<FixAcceptor>();

        return services;
    }
}
