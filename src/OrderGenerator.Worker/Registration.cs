using Microsoft.Extensions.DependencyInjection;
using OrderGenerator.Worker.Fix;

namespace OrderGenerator.Worker;

public static class Registration
{
    public static IServiceCollection AddOrderProcessingWorker(this IServiceCollection services)
    {
        services.AddSingleton<FixInitiator>();
        services.AddSingleton<OrderGenerator.Application.IExposureTransport>(sp => sp.GetRequiredService<FixInitiator>());
        services.AddSingleton<IOrderTransport>(sp => sp.GetRequiredService<FixInitiator>());
        services.AddHostedService<FixReportConsumer>();
        services.AddHostedService(sp => sp.GetRequiredService<FixInitiator>());
        services.AddHostedService<OrderRecoveryWorker>();

        return services;
    }
}
