using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OrderGenerator.Application.Mapping;
using OrderGenerator.Application.Transactions;
using OrderGenerator.Application.State;
using OrderGenerator.Domain.Orders;
using OrderGenerator.Domain.ValueObjects;

namespace OrderGenerator.Application.UseCases;

public sealed class ConfirmOrderUseCase(
    IOrderIntentUnitOfWorkFactory transactions,
    OrderConfirmationNotifier notifications,
    ILogger<ConfirmOrderUseCase> log,
    IValidator<ConfirmOrderInput> validator) : IConfirmOrderUseCase
{
    public Result<ConfirmOrderOutput> Execute(ConfirmOrderInput input)
    {
        var validationError = validator.ValidateInput(input, "ValidationFailed");

        if (validationError is not null)
        {
            return Result<ConfirmOrderOutput>.Failure(validationError);
        }

        var outcome = PersistConfirmation(input);

        return CompleteConfirmation(input, outcome);
    }

    private Result<ConfirmOrderOutput> CompleteConfirmation(ConfirmOrderInput input, ConfirmationOutcome outcome)
    {
        switch (outcome)
        {
            case ConfirmationOutcome.Uncorrelated:
                log.LogWarning("Uncorrelated report {ClOrdID}", input.Id);
                return Result<ConfirmOrderOutput>.Failure(new("OrderNotFound", "Report identity is unknown"));
            case ConfirmationOutcome.Contradiction:
                log.LogError("Contradictory report {ClOrdID}", input.Id);
                return Result<ConfirmOrderOutput>.Failure(
                    new(
                        "ContradictoryReport",
                        "Report contradicts the recorded decision"));
            case ConfirmationOutcome.Confirmed confirmed:
                notifications.Publish(confirmed.Id);
                var elapsedMs = confirmed.Duration.TotalMilliseconds;
                OrderApplicationMetrics.ConfirmationDuration.Record(elapsedMs);
                log.LogInformation(
                    "Report {ClOrdID} decision {Decision} FIX roundTripMs {RoundTripMs}",
                    input.Id,
                    input.Status,
                    elapsedMs);
                return Result<ConfirmOrderOutput>.Success(new(Applied: true));
            case ConfirmationOutcome.Unchanged:
                return Result<ConfirmOrderOutput>.Success(new(Applied: false));
            default:
                throw new UnreachableException();
        }
    }

    private ConfirmationOutcome PersistConfirmation(ConfirmOrderInput input)
    {
        if (!OrderIntentId.TryCreate(input.Id, out var id))
        {
            return new ConfirmationOutcome.Uncorrelated();
        }

        using var work = transactions.BeginIntent(id.Value);
        var previous = work.Intents.Find(id.Value);

        if (previous is null)
        {
            return new ConfirmationOutcome.Uncorrelated();
        }

        var transition = previous.ToDomain().Confirm(input.ToConfirmation());

        if (transition.Change == ConfirmationChange.EquivalentDuplicate)
        {
            return new ConfirmationOutcome.Unchanged();
        }

        work.Intents.Save(transition.Next.ToStored(previous.LastAttempt));
        work.Commit();

        return transition.Change switch
        {
            ConfirmationChange.Confirmed => new ConfirmationOutcome.Confirmed(
                id.Value,
                ConfirmationElapsed(
                    previous.LastAttempt)),
            ConfirmationChange.Contradiction => new ConfirmationOutcome.Contradiction(),
            _ => throw new UnreachableException()
        };
    }

    private static TimeSpan ConfirmationElapsed(DateTimeOffset? lastAttempt)
    {
        var now = DateTimeOffset.UtcNow;

        return lastAttempt is { } attempted && now > attempted ? now - attempted : TimeSpan.Zero;
    }
}
