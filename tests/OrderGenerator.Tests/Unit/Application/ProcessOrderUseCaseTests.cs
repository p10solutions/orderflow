using OrderAccumulator.Application.Repositories;
using OrderAccumulator.Application.Transactions;
using OrderAccumulator.Domain;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class ProcessOrderUseCaseTests
{
    private static readonly Order Order = new(
        "PETR4",
        OrderSide.Buy,
        1,
        10m);

    [Fact]
    public void New_order_commits_decision_and_exposure_as_one_unit()
    {
        var work = new Work();
        var result = new ProcessOrderUseCase(new Factory(work), new ProcessOrderInputValidator()).Execute(
            (Order).ToTestProcessInput(
                "session",
                "id",
                "fingerprint")).Value.ToTestDecision();
        Assert.True(result.Accepted);
        Assert.Equal(10m, work.ExposureRepository.Value);
        var saved = Assert.Single(work.DecisionRepository.Added);
        Assert.Equal(result, saved.Decision);
        Assert.Equal("fingerprint", saved.Fingerprint);
        Assert.Equal(1, work.Commits);
        Assert.True(work.Disposed);
    }

    [Fact]
    public void Equivalent_duplicate_returns_original_without_writing()
    {
        var original = new Decision(
            "order",
            "exec",
            true,
            "Accepted",
            0m,
            10m);
        var work = new Work();
        work.DecisionRepository.Existing = new(Order, "fingerprint", original);
        work.ExposureRepository.Value = 10m;
        Assert.Equal(
            original,
            new ProcessOrderUseCase(new Factory(work), new ProcessOrderInputValidator()).Execute(
                (Order).ToTestProcessInput(
                    "session",
                    "id",
                    "fingerprint")).Value.ToTestDecision());
        Assert.Empty(work.DecisionRepository.Added);
        Assert.Equal(0, work.ExposureRepository.Reads);
        Assert.Equal(0, work.ExposureRepository.Writes);
        Assert.Equal(0, work.Commits);
        Assert.True(work.Disposed);
    }

    [Fact]
    public void Conflict_preserves_original_decision_and_exposure()
    {
        var original = new RecordedDecision(Order, "original", new(
            "order",
            "exec",
            true,
            "Accepted",
            0m,
            10m));
        var work = new Work();
        work.DecisionRepository.Existing = original;
        work.ExposureRepository.Value = 10m;
        var result = new ProcessOrderUseCase(new Factory(work), new ProcessOrderInputValidator()).Execute(
            (Order).ToTestProcessInput(
                "session",
                "id",
                "conflicting")).Value.ToTestDecision();
        Assert.False(result.Accepted);
        Assert.Equal("ClOrdID payload conflict", result.Reason);
        Assert.Same(original, work.DecisionRepository.Existing);
        Assert.Empty(work.DecisionRepository.Added);
        Assert.Equal(0, work.ExposureRepository.Writes);
        Assert.Equal(0, work.Commits);
    }

    [Fact]
    public void Rejection_is_recorded_without_updating_exposure()
    {
        var work = new Work();
        work.ExposureRepository.Value = RiskPolicy.ExposureLimit;
        var result = new ProcessOrderUseCase(new Factory(work), new ProcessOrderInputValidator()).Execute(
            (Order).ToTestProcessInput(
                "session",
                "id")).Value.ToTestDecision();
        Assert.False(result.Accepted);
        Assert.Equal("Exposure limit exceeded", result.Reason);
        Assert.Equal(result, Assert.Single(work.DecisionRepository.Added).Decision);
        Assert.Equal(0, work.ExposureRepository.Writes);
        Assert.Equal(1, work.Commits);
    }

    [Fact]
    public void Commit_failure_propagates_and_disposes_the_transaction()
    {
        var work = new Work
        {
            FailCommit = true
        };
        Assert.Throws<IOException>(
            () => new ProcessOrderUseCase(new Factory(work), new ProcessOrderInputValidator()).Execute(
                (Order).ToTestProcessInput(
                    "session",
                    "id")).Value.ToTestDecision());
        Assert.True(work.Disposed);
        Assert.Equal(0, work.Commits);
    }

    [Theory]
    [InlineData(null, "id")]
    [InlineData("", "id")]
    [InlineData(" ", "id")]
    [InlineData("session", null)]
    [InlineData("session", " ")]
    public void Missing_correlation_returns_failure_without_opening_transaction(string? session, string? id)
    {
        var useCase = new ProcessOrderUseCase(new NeverBegin(), new ProcessOrderInputValidator());
        var result = useCase.Execute((Order).ToTestProcessInput(session!, id!));
        Assert.True(result.IsFailure);
        Assert.Equal("ValidationFailed", result.Error.Code);
        Assert.NotEmpty(result.Error.Fields!);
    }

    [Fact]
    public void Missing_input_returns_failure_without_opening_transaction()
    {
        var useCase = new ProcessOrderUseCase(new NeverBegin(), new ProcessOrderInputValidator());
        Assert.True(useCase.Execute(null!).IsFailure);
        Assert.True(useCase.Execute(new(
            "",
            "id",
            "PETR4",
            1,
            1,
            10m)).IsFailure);
    }

    [Fact]
    public void Invalid_financial_payload_is_still_a_durable_deduplicated_decision()
    {
        var work = new Work();
        var result = new ProcessOrderUseCase(new Factory(work), new ProcessOrderInputValidator()).Execute(
            (Order with
            {
                Quantity = 0
            }).ToTestProcessInput(
                "session",
                "id"));
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.ToTestDecision().Accepted);
        Assert.Equal(result.Value.ToTestDecision(), Assert.Single(work.DecisionRepository.Added).Decision);
        Assert.Equal(1, work.Commits);
        Assert.Equal(0, work.ExposureRepository.Writes);
    }

    private sealed class NeverBegin : IOrderUnitOfWorkFactory
    {
        public IOrderUnitOfWork Begin() => throw new InvalidOperationException("Validation must precede transaction");
    }

    private sealed class Factory(Work work) : IOrderUnitOfWorkFactory
    {
        public IOrderUnitOfWork Begin() => work;
    }

    private sealed class Work : IOrderUnitOfWork
    {
        public Decisions DecisionRepository { get; } = new();
        public Exposures ExposureRepository { get; } = new();
        public IDecisionRepository Decisions => DecisionRepository;
        public IExposureRepository Exposures => ExposureRepository;
        public int Commits
        {
            get; private set;
        }
        public bool Disposed
        {
            get; private set;
        }
        public bool FailCommit
        {
            get; init;
        }

        public void Commit()
        {
            if (FailCommit)
            {
                throw new IOException("Commit failed");
            }

            Commits++;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class Decisions : IDecisionRepository
    {
        public RecordedDecision? Existing
        {
            get; set;
        }
        public List<RecordedDecision> Added { get; } = [];

        public RecordedDecision? Find(string session, string clOrdId) => Existing;

        public void Add(
            string session,
            string clOrdId,
            RecordedDecision record) => Added.Add(record);
    }

    private sealed class Exposures : IExposureRepository
    {
        public decimal Value
        {
            get; set;
        }
        public int Writes
        {
            get; private set;
        }
        public int Reads
        {
            get; private set;
        }

        public decimal Get(string symbol)
        {
            Reads++;

            return Value;
        }

        public void Set(string symbol, decimal exposure)
        {
            Value = exposure;
            Writes++;
        }
    }
}
