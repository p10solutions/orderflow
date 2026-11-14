using Microsoft.Extensions.Logging.Abstractions;
using OrderGenerator;
using OrderGenerator.Application.Repositories;
using OrderGenerator.Application.State;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class OrderConfirmationNotifierTests
{
    private sealed class Transport : IOrderTransport
    {
        public bool Connected => true;

        public int Sends;

        public bool Send(string id, OrderPayload payload)
        {
            Sends++;

            return false;
        }
    }

    private sealed class OnReadRepository(IOrderIntentRepository inner) : IOrderIntentRepository
    {
        public Action? AfterRead;

        public StoredIntent? Find(string id)
        {
            var value = inner.Find(id);
            Interlocked.Exchange(ref AfterRead, null)?.Invoke();

            return value;
        }

        public IReadOnlyList<StoredIntent> GetAll() => inner.GetAll();

        public int Count() => inner.Count();

        public IReadOnlyList<StoredIntent> FindPendingDue(DateTimeOffset cutoff, int limit) => inner.FindPendingDue(
            cutoff,
            limit);

        public void Save(StoredIntent intent) => inner.Save(intent);
    }

    [Fact]
    public async Task Waiters_are_removed_after_timeout_cancellation_and_broadcast_confirmation()
    {
        var repository = new InMemoryOrderIntentRepository();
        var transactions = TestOrderIntentUnitOfWorkFactory.For(repository);
        var notifications = new OrderConfirmationNotifier();
        var get = new GetOrderUseCase(repository, new GetOrderInputValidator());
        var submit = new SubmitOrderUseCase(
            transactions,
            new(),
            notifications,
            new Transport(),
            get,
            NullLogger<SubmitOrderUseCase>.Instance,
            new SubmitOrderInputValidator());
        var input = new SubmitOrderInput(
            Guid.NewGuid().ToString("D"),
            "PETR4",
            "Buy",
            1,
            "10.00",
            TimeSpan.Zero);
        await submit.ExecuteAsync(input, default);
        Assert.Equal(0, notifications.ActiveWaiterCount);
        using var cancellation = new CancellationTokenSource();
        var waiting = submit.ExecuteAsync(
            input with
            {
                ConfirmationTimeout = TimeSpan.FromMinutes(1)
            },
            cancellation.Token);
        Assert.Equal(1, notifications.ActiveWaiterCount);
        cancellation.Cancel();
        Assert.Equal("PendingConfirmation", (await waiting).Value.Status);
        Assert.Equal(0, notifications.ActiveWaiterCount);
        var first = submit.ExecuteAsync(input with
        {
            ConfirmationTimeout = TimeSpan.FromMinutes(1)
        }, default);
        var second = submit.ExecuteAsync(input with
        {
            ConfirmationTimeout = TimeSpan.FromMinutes(1)
        }, default);
        Assert.Equal(2, notifications.ActiveWaiterCount);
        var confirm = new ConfirmOrderUseCase(
            transactions,
            notifications,
            NullLogger<ConfirmOrderUseCase>.Instance,
            new ConfirmOrderInputValidator());
        confirm.Execute(new(
            input.Id,
            "Accepted",
            "order",
            "exec"));
        Assert.Equal("Accepted", (await first.WaitAsync(TimeSpan.FromSeconds(2))).Value.Status);
        Assert.Equal("Accepted", (await second.WaitAsync(TimeSpan.FromSeconds(2))).Value.Status);
        Assert.Equal(0, notifications.ActiveWaiterCount);
    }

    [Fact]
    public async Task Confirmation_between_subscription_and_stale_read_is_not_lost()
    {
        var repository = new InMemoryOrderIntentRepository();
        var id = Guid.NewGuid().ToString("D");
        repository.Save(new(
            id,
            new(
                "PETR4",
                "Buy",
                1,
                10m),
            null,
            null));
        var transactions = TestOrderIntentUnitOfWorkFactory.For(repository);
        var notifications = new OrderConfirmationNotifier();
        var onRead = new OnReadRepository(repository);
        var get = new GetOrderUseCase(onRead, new GetOrderInputValidator());
        var confirm = new ConfirmOrderUseCase(
            transactions,
            notifications,
            NullLogger<ConfirmOrderUseCase>.Instance,
            new ConfirmOrderInputValidator());
        onRead.AfterRead = () =>
        {
            Assert.Equal(1, notifications.ActiveWaiterCount);
            Assert.True(confirm.Execute(new(
                id,
                "Accepted",
                "order",
                "exec")).IsSuccess);
        };
        var transport = new Transport();
        var submit = new SubmitOrderUseCase(
            transactions,
            new(),
            notifications,
            transport,
            get,
            NullLogger<SubmitOrderUseCase>.Instance,
            new SubmitOrderInputValidator());
        var result = await submit.ExecuteAsync(new(
            id,
            "PETR4",
            "Buy",
            1,
            "10.00",
            TimeSpan.FromMinutes(1)), default).WaitAsync(
            TimeSpan.FromSeconds(2));
        Assert.Equal("Accepted", result.Value.Status);
        Assert.Equal(0, transport.Sends);
        Assert.Equal(0, notifications.ActiveWaiterCount);
    }
}
