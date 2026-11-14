using Microsoft.Extensions.Logging;
using OrderGenerator.Application.State;
using OrderGenerator.Application.Transactions;

namespace OrderGenerator.Application.UseCases;

public sealed class RecoverPendingOrdersUseCase(
    IOrderIntentUnitOfWorkFactory transactions,
    IOrderTransport transport,
    ILogger<RecoverPendingOrdersUseCase> log,
    IValidator<RecoverPendingOrdersInput> validator) : IRecoverPendingOrdersUseCase
{
    public Result<RecoverPendingOrdersOutput> Execute(RecoverPendingOrdersInput input)
    {
        var error = validator.ValidateInput(input);

        if (error is not null)
        {
            return Result<RecoverPendingOrdersOutput>.Failure(error);
        }

        if (!transport.Connected)
        {
            return Result<RecoverPendingOrdersOutput>.Success(new(0));
        }

        var batch = ReservePendingBatch(input);
        SendBatch(batch);

        return Result<RecoverPendingOrdersOutput>.Success(new(batch.Count));
    }

    private IReadOnlyList<StoredIntent> ReservePendingBatch(RecoverPendingOrdersInput input)
    {
        using var work = transactions.BeginRecovery();
        var cutoff = input.Now.UtcTicks < input.RetryInterval.Ticks
            ? DateTimeOffset.MinValue
            : input.Now - input.RetryInterval;
        var batch = work.Intents.FindPendingDue(cutoff, input.BatchSize);

        foreach (var item in batch)
        {
            work.Intents.Save(item with
            {
                LastAttempt = input.Now.ToUniversalTime()
            });
        }

        work.Commit();

        return batch;
    }

    private void SendBatch(IReadOnlyList<StoredIntent> batch)
    {
        foreach (var item in batch)
        {
            OrderApplicationMetrics.RecoveryAttempts.Add(1);

            try
            {
                transport.Send(item.Id, item.Payload);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Recovery send uncertain {ClOrdID}", item.Id);
            }
        }
    }
}
