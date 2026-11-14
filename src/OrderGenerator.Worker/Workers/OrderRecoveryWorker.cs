using Microsoft.Extensions.Hosting;
using OrderGenerator.Application.UseCases;
using Microsoft.Extensions.Logging;
using Npgsql;
using OrderGenerator.Infrastructure.Persistence;

namespace OrderGenerator.Worker;

public sealed class OrderRecoveryWorker(
    IRecoverPendingOrdersUseCase recover,
    PostgresDatabase.HostLease lease,
    ILogger<OrderRecoveryWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            lease.EnsureHeld();

            try
            {
                RecoverPendingOrders();
            }
            catch (NpgsqlException error) when (error.IsTransient)
            {
                lease.EnsureHeld();
                log.LogWarning(error, "Recovery temporarily unavailable; retrying on the next cycle");
            }
        }
    }

    private void RecoverPendingOrders()
    {
        var result = recover.Execute(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(15)));

        if (result.IsFailure)
        {
            log.LogWarning("Recovery refused {Code}: {Reason}", result.Error.Code, result.Error.Message);

            return;
        }

        var count = result.Value.AttemptedCount;

        if (count != 0)
        {
            log.LogInformation("Recovery attempted {Count} pending FIX orders", count);
        }
    }
}
