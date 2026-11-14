using System.Diagnostics;
using System.Diagnostics.Metrics;
using OrderAccumulator.Application.Mapping;
using OrderAccumulator.Application.Repositories;
using OrderAccumulator.Application.Transactions;
using OrderAccumulator.Domain;

namespace OrderAccumulator.Application.UseCases.ProcessOrder;

public sealed class ProcessOrderUseCase(
    IOrderUnitOfWorkFactory unitOfWorkFactory,
    IValidator<ProcessOrderInput> validator) : IProcessOrderUseCase
{
    private static readonly Meter Meter = new("OrderFlow.Accumulator");
    private static readonly Counter<long> Decisions = Meter.CreateCounter<long>(
        "orderflow.decisions",
        description: "Decisions returned, including equivalent retries");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "orderflow.decision.duration",
        "ms");

    public Result<ProcessOrderOutput> Execute(ProcessOrderInput input)
    {
        var validationError = validator.ValidateInput(input);

        if (validationError is not null)
        {
            return Result<ProcessOrderOutput>.Failure(validationError);
        }

        var order = input.ToDomain();
        var started = Stopwatch.GetTimestamp();
        var decision = ProcessAtomically(input, order);
        Decisions.Add(1, new KeyValuePair<string, object?>("accepted", decision.Accepted));
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);

        return Result<ProcessOrderOutput>.Success(decision.ToOutput());
    }

    private Decision ProcessAtomically(ProcessOrderInput input, Order order)
    {
        using var work = unitOfWorkFactory.Begin();
        var previous = work.Decisions.Find(input.Session, input.ClOrdId);

        if (previous is not null)
        {
            return ResolveExisting(
                previous,
                order,
                input.Fingerprint,
                work.Exposures);
        }

        return DecideAndRecord(work, input, order);
    }

    private static Decision ResolveExisting(
        RecordedDecision previous,
        Order order,
        string? fingerprint,
        IExposureRepository exposures)
    {
        if (previous.Matches(order, fingerprint))
        {
            return previous.Decision;
        }

        var currentExposure = exposures.Get(order.Symbol ?? string.Empty);

        return Decision.RejectPayloadConflict(currentExposure);
    }

    private static Decision DecideAndRecord(
        IOrderUnitOfWork work,
        ProcessOrderInput input,
        Order order)
    {
        var symbol = order.Symbol ?? string.Empty;
        var currentExposure = work.Exposures.Get(symbol);
        var decision = RiskPolicy.Decide(order, currentExposure);

        if (decision.Accepted)
        {
            work.Exposures.Set(symbol, decision.After);
        }

        work.Decisions.Add(input.Session, input.ClOrdId, new(order, input.Fingerprint, decision));
        work.Commit();

        return decision;
    }
}
