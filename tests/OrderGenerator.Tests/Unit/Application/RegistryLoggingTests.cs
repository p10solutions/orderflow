using Microsoft.Extensions.Logging;
using OrderGenerator;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class RegistryLoggingTests
{
    private sealed class Transport : IOrderTransport
    {
        public bool Connected => true;

        public bool Send(string id, OrderPayload order) => false;
    }

    private sealed class BlockingLogger : ILogger<TestOrderApplication>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).StartsWith("Report ", StringComparison.Ordinal))
            {
                return;
            }

            Entered.TrySetResult();

            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Test logger was not released");
            }
        }
    }

    [Fact]
    public async Task R5_Slow_log_provider_does_not_hold_registry_lock()
    {
        var logger = new BlockingLogger();
        var registry = new TestOrderApplication(new Transport(), logger);
        var order = new OrderRequest(
            Guid.NewGuid().ToString("D"),
            "PETR4",
            "Buy",
            1,
            "1.00");
        await registry.Submit(order, TimeSpan.Zero, default);
        var confirmation = Task.Run(() => registry.Confirm(new(
            order.Id,
            "Accepted",
            "order",
            "exec")));

        try
        {
            await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var result = await Task.Run(() => registry.Get(order.Id)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("Accepted", result!.Status);
            Assert.Equal(
                SubmissionStatus.Confirmed,
                (await registry.Submit(order, TimeSpan.Zero, default).WaitAsync(
                    TimeSpan.FromSeconds(2))).Status);
        }
        finally
        {
            logger.Release.Set();
            await confirmation;
            logger.Release.Dispose();
        }
    }
}
