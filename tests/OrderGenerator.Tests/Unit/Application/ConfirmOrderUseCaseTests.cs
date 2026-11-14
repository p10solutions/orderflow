using Microsoft.Extensions.Logging.Abstractions;
using OrderGenerator;
using OrderGenerator.Application.Repositories;
using OrderGenerator.Application.State;
using OrderGenerator.Domain.Orders;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class ConfirmOrderUseCaseTests
{
    private sealed class Repository : IOrderIntentRepository
    {
        private readonly InMemoryOrderIntentRepository inner = new();
        public bool FailWrites
        {
            get; set;
        }
        public int Writes
        {
            get; private set;
        }

        public StoredIntent? Find(string id) => inner.Find(id);

        public IReadOnlyList<StoredIntent> GetAll() => inner.GetAll();

        public int Count() => inner.Count();

        public IReadOnlyList<StoredIntent> FindPendingDue(DateTimeOffset cutoff, int limit) => inner.FindPendingDue(
            cutoff,
            limit);

        public void Save(StoredIntent intent)
        {
            if (FailWrites)
            {
                throw new IOException("Injected persistence failure");
            }

            inner.Save(intent);
            Writes++;
        }
    }

    private sealed class Transport : IOrderTransport
    {
        public bool Connected => true;

        public bool Send(string id, OrderPayload order) => true;
    }

    private sealed class Fixture
    {
        public Repository Repository { get; } = new();
        public SubmitOrderUseCase Submit
        {
            get;
        }
        public GetOrderUseCase Get
        {
            get;
        }
        public ConfirmOrderUseCase Confirm
        {
            get;
        }
        public OrderRequest Request
        {
            get;
        } = new(
            Guid.NewGuid().ToString("D"),
            "PETR4",
            "Buy",
            1,
            "10.00");
        public OrderResult Accepted => new(
            Request.Id,
            "Accepted",
            "order",
            "exec");
        public OrderResult Rejected => new(
            Request.Id,
            "Rejected",
            "other-order",
            "other-exec",
            "Rejected");

        public Fixture()
        {
            var transactions = TestOrderIntentUnitOfWorkFactory.For(Repository);
            var notifications = new OrderConfirmationNotifier();
            Get = new(Repository, new GetOrderInputValidator());
            Submit = new(
                transactions,
                new OrderIntentOptions(),
                notifications,
                new Transport(),
                Get,
                NullLogger<SubmitOrderUseCase>.Instance,
                new SubmitOrderInputValidator());
            Confirm = new(
                transactions,
                notifications,
                NullLogger<ConfirmOrderUseCase>.Instance,
                new ConfirmOrderInputValidator());
        }
    }

    [Fact]
    public async Task Failed_confirmation_does_not_release_waiter_or_publish_terminal_state()
    {
        var fixture = new Fixture();
        var waiting = fixture.Submit.ExecuteAsync(fixture.Request.ToSubmitOrderInput(TimeSpan.FromSeconds(10)), default);
        fixture.Repository.FailWrites = true;
        Assert.Throws<IOException>(() => fixture.Confirm.Execute((fixture.Accepted).ToConfirmOrderInput()));
        Assert.False(waiting.IsCompleted);
        Assert.Equal("PendingConfirmation", fixture.Get.Execute(new(fixture.Request.Id)).Value.Status);
        Assert.Null(fixture.Repository.Find(fixture.Request.Id)!.Result);
        fixture.Repository.FailWrites = false;
        fixture.Confirm.Execute((fixture.Accepted).ToConfirmOrderInput());
        var result = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.IsSuccess);
        Assert.Equal(fixture.Accepted, result.Value.ToTestOrderResult());
    }

    [Fact]
    public async Task Failed_contradiction_write_preserves_original_decision_and_diagnostic()
    {
        var fixture = new Fixture();
        await fixture.Submit.ExecuteAsync(fixture.Request.ToSubmitOrderInput(TimeSpan.Zero), default);
        fixture.Confirm.Execute((fixture.Accepted).ToConfirmOrderInput());
        fixture.Repository.FailWrites = true;
        Assert.Throws<IOException>(() => fixture.Confirm.Execute((fixture.Rejected).ToConfirmOrderInput()));
        Assert.Equal(fixture.Accepted, fixture.Get.Execute(new(fixture.Request.Id)).Value.ToTestOrderResult());
        Assert.Equal(fixture.Accepted, fixture.Repository.Find(fixture.Request.Id)!.Result);
        Assert.Null(fixture.Repository.Find(fixture.Request.Id)!.Diagnostic);
    }

    [Fact]
    public async Task Equivalent_replay_does_not_attempt_a_write()
    {
        var fixture = new Fixture();
        await fixture.Submit.ExecuteAsync(fixture.Request.ToSubmitOrderInput(TimeSpan.Zero), default);
        fixture.Confirm.Execute((fixture.Accepted).ToConfirmOrderInput());
        fixture.Repository.FailWrites = true;
        var replay = fixture.Confirm.Execute((fixture.Accepted).ToConfirmOrderInput());
        Assert.True(replay.IsSuccess);
        Assert.False(replay.Value.Applied);
        Assert.Equal(2, fixture.Repository.Writes);
        Assert.Equal(fixture.Accepted, fixture.Get.Execute(new(fixture.Request.Id)).Value.ToTestOrderResult());
    }

    [Fact]
    public async Task Concurrent_conflicting_confirmations_preserve_one_terminal_decision()
    {
        var fixture = new Fixture();
        await fixture.Submit.ExecuteAsync(fixture.Request.ToSubmitOrderInput(TimeSpan.Zero), default);
        await Task.WhenAll(
            Task.Run(() => fixture.Confirm.Execute((fixture.Accepted).ToConfirmOrderInput())),
            Task.Run(
                () => fixture.Confirm.Execute(
                    (fixture.Rejected).ToConfirmOrderInput())));
        var persisted = fixture.Repository.Find(fixture.Request.Id)!;
        Assert.Contains(persisted.Result, new[] { fixture.Accepted, fixture.Rejected });
        Assert.Equal(
            persisted.Result! with
            {
                Diagnostic = persisted.Diagnostic
            },
            fixture.Get.Execute(
                new(
                    fixture.Request.Id)).Value.ToTestOrderResult());
        Assert.Equal(OrderIntent.ContradictoryReportDiagnostic, persisted.Diagnostic);
    }

    [Fact]
    public async Task Incomplete_confirmation_returns_failure_and_later_valid_confirmation_can_complete()
    {
        var fixture = new Fixture();
        await fixture.Submit.ExecuteAsync(fixture.Request.ToSubmitOrderInput(TimeSpan.Zero), default);
        var invalid = fixture.Confirm.Execute(
            (fixture.Accepted with
            {
                OrderId = null,
                ExecId = " "
            }).ToConfirmOrderInput());
        Assert.True(invalid.IsFailure);
        Assert.Equal("ValidationFailed", invalid.Error.Code);
        Assert.Equal(1, fixture.Repository.Writes);
        Assert.Equal("PendingConfirmation", fixture.Get.Execute(new(fixture.Request.Id)).Value.Status);
        var valid = fixture.Confirm.Execute((fixture.Accepted).ToConfirmOrderInput());
        Assert.True(valid.Value.Applied);
        Assert.Equal(fixture.Accepted, fixture.Get.Execute(new(fixture.Request.Id)).Value.ToTestOrderResult());
    }

    [Fact]
    public async Task Confirmation_matches_canonical_identity()
    {
        var fixture = new Fixture();
        await fixture.Submit.ExecuteAsync(fixture.Request.ToSubmitOrderInput(TimeSpan.Zero), default);
        fixture.Confirm.Execute(
            (fixture.Accepted with
            {
                Id = fixture.Request.Id.ToUpperInvariant()
            }).ToConfirmOrderInput());
        Assert.Equal(fixture.Accepted, fixture.Get.Execute(new(fixture.Request.Id)).Value.ToTestOrderResult());
    }
}
