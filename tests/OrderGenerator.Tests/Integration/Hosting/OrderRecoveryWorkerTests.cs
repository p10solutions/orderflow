using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OrderFlow.Shared;
using OrderGenerator.Infrastructure.Persistence;
using OrderGenerator.Worker;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class OrderRecoveryWorkerTests
{
    [Fact]
    public async Task Transient_failure_retries_on_next_cycle_without_stopping_the_worker()
    {
        using var storage = new PostgresTestDatabase();
        var database = new PostgresDatabase(storage.ConnectionString);
        using var lease = database.AcquireHostLease();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var useCase = new Recovery(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new PostgresException("Connection interrupted", "FATAL", "FATAL", "57P01");
            }

            recovered.TrySetResult();

            return Result<RecoverPendingOrdersOutput>.Success(new(0));
        });
        using var worker = new OrderRecoveryWorker(useCase, lease, NullLogger<OrderRecoveryWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        try
        {
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(calls >= 2);
            Assert.False(worker.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Permanent_database_failure_is_not_swallowed()
    {
        using var storage = new PostgresTestDatabase();
        var database = new PostgresDatabase(storage.ConnectionString);
        using var lease = database.AcquireHostLease();
        var useCase = new Recovery(() => throw new PostgresException("Invalid schema", "ERROR", "ERROR", "42P01"));
        using var worker = new OrderRecoveryWorker(useCase, lease, NullLogger<OrderRecoveryWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<PostgresException>(
            () => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("42P01", error.SqlState);
    }

    [Fact]
    public async Task Lost_host_lease_stops_before_running_recovery()
    {
        using var storage = new PostgresTestDatabase();
        var name = "lease-test-" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(storage.ConnectionString)
        {
            ApplicationName = name
        };
        var database = new PostgresDatabase(settings.ToString());
        using var lease = database.AcquireHostLease();
        var calls = 0;
        var useCase = new Recovery(() =>
        {
            Interlocked.Increment(ref calls);

            return Result<RecoverPendingOrdersOutput>.Success(new(0));
        });
        using var worker = new OrderRecoveryWorker(useCase, lease, NullLogger<OrderRecoveryWorker>.Instance);
        using var admin = new NpgsqlConnection(storage.ConnectionString);
        await admin.OpenAsync();
        await using var terminate = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name=@name",
            admin);
        terminate.Parameters.AddWithValue("name", name);
        Assert.True((bool)(await terminate.ExecuteScalarAsync())!);

        await worker.StartAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<NpgsqlException>(
            () => worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, calls);
    }

    private sealed class Recovery(Func<Result<RecoverPendingOrdersOutput>> execute) : IRecoverPendingOrdersUseCase
    {
        public Result<RecoverPendingOrdersOutput> Execute(RecoverPendingOrdersInput input) => execute();
    }
}
