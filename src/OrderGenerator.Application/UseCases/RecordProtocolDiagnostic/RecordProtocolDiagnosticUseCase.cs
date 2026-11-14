using Microsoft.Extensions.Logging;
using OrderGenerator.Application.Mapping;
using OrderGenerator.Application.Transactions;
using OrderGenerator.Domain.ValueObjects;

namespace OrderGenerator.Application.UseCases;

public sealed class RecordProtocolDiagnosticUseCase(
    IOrderIntentUnitOfWorkFactory transactions,
    ILogger<RecordProtocolDiagnosticUseCase> log,
    IValidator<RecordProtocolDiagnosticInput> validator) : IRecordProtocolDiagnosticUseCase
{
    public Result<RecordProtocolDiagnosticOutput> Execute(RecordProtocolDiagnosticInput input)
    {
        var validationError = validator.ValidateInput(input, "ValidationFailed");

        if (validationError is not null)
        {
            return Result<RecordProtocolDiagnosticOutput>.Failure(validationError);
        }

        var result = PersistDiagnostic(input);
        log.LogWarning("FIX diagnostic {ClOrdID}: {Diagnostic}", input.Id, input.Message);

        return result;
    }

    private Result<RecordProtocolDiagnosticOutput> PersistDiagnostic(RecordProtocolDiagnosticInput input)
    {
        if (!OrderIntentId.TryCreate(input.Id, out var id))
        {
            return Result<RecordProtocolDiagnosticOutput>.Failure(
                new("OrderNotFound", "Diagnostic identity is unknown"));
        }

        using var work = transactions.BeginIntent(id.Value);
        var previous = work.Intents.Find(id.Value);

        if (previous is null)
        {
            return Result<RecordProtocolDiagnosticOutput>.Failure(
                new("OrderNotFound", "Diagnostic identity is unknown"));
        }

        var next = previous.ToDomain().RecordDiagnostic(input.Message);
        work.Intents.Save(next.ToStored(previous.LastAttempt));
        work.Commit();

        return Result<RecordProtocolDiagnosticOutput>.Success(new(id.Value));
    }
}
