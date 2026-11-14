using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using OrderGenerator.Application.Mapping;
using OrderGenerator.Application.State;
using OrderGenerator.Application.Transactions;
using OrderGenerator.Domain.Orders;
using OrderGenerator.Domain.ValueObjects;

namespace OrderGenerator.Application.UseCases;

public sealed class SubmitOrderUseCase(
    IOrderIntentUnitOfWorkFactory transactions,
    OrderIntentOptions options,
    OrderConfirmationNotifier notifications,
    IOrderTransport transport,
    IGetOrderUseCase get,
    ILogger<SubmitOrderUseCase> log,
    IValidator<SubmitOrderInput> validator) : ISubmitOrderUseCase
{
    public async Task<Result<SubmitOrderOutput>> ExecuteAsync(
        SubmitOrderInput input,
        CancellationToken cancellationToken)
    {
        var error = validator.ValidateInput(input);

        if (error is not null)
        {
            return Result<SubmitOrderOutput>.Failure(error);
        }

        var payload = new OrderPayload(
            input.Symbol,
            input.Side,
            input.Quantity,
            decimal.Parse(
                input.Price,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture));
        var identity = OrderIntentId.Parse(input.Id);
        var admission = Admit(identity, payload);

        if (admission is SubmissionAdmission.Rejected rejected)
        {
            return rejected.Result;
        }

        if (admission is SubmissionAdmission.Registered)
        {
            SendRegisteredOrder(identity.Value, payload);
        }

        return await WaitForConfirmation(identity.Value, input.ConfirmationTimeout, cancellationToken);
    }

    private void SendRegisteredOrder(string id, OrderPayload payload)
    {
        OrderApplicationMetrics.Admitted.Add(1);

        try
        {
            if (!transport.Send(id, payload))
            {
                log.LogWarning("Send uncertain {ClOrdID}", id);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Send uncertain {ClOrdID}", id);
        }
    }

    private SubmissionAdmission Admit(OrderIntentId identity, OrderPayload payload)
    {
        using var work = transactions.BeginIntent(identity.Value);
        var previous = work.Intents.Find(identity.Value);

        if (previous is not null)
        {
            return previous.ToDomain().Matches(payload)
                ? new SubmissionAdmission.Existing()
                : new SubmissionAdmission.Rejected(
                    Result<SubmitOrderOutput>.Failure(
                        new(
                            "IdentityConflict",
                            "Identity payload conflict")));
        }

        if (!transport.Connected)
        {
            return new SubmissionAdmission.Rejected(
                Result<SubmitOrderOutput>.Failure(
                    new(
                        "FixUnavailable",
                        "FIX session unavailable")));
        }

        work.LockAdmission();

        if (work.Intents.Count() >= options.Capacity)
        {
            return new SubmissionAdmission.Rejected(
                Result<SubmitOrderOutput>.Failure(
                    new(
                        "RegistryCapacityReached",
                        "Order registry capacity reached; operator intervention required")));
        }

        work.Intents.Save(OrderIntent.Pending(identity, payload).ToStored(DateTimeOffset.UtcNow));
        work.Commit();

        return new SubmissionAdmission.Registered();
    }

    private async Task<Result<SubmitOrderOutput>> WaitForConfirmation(
        string id,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        // Subscribe before reading: a confirmation committed before subscription is seen by the read;
        // one committed afterwards either is read or wakes this waiter. No notification gap.
        using var subscription = notifications.Subscribe(id);
        var current = get.Execute(new(id));

        if (current.IsFailure)
        {
            return Result<SubmitOrderOutput>.Failure(current.Error);
        }

        if (current.Value.Status != "PendingConfirmation")
        {
            return Result<SubmitOrderOutput>.Success(current.Value.ToSubmitOrderOutput());
        }

        try
        {
            await subscription.Completion.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            log.LogWarning("Confirmation timeout {ClOrdID}", id);
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("HTTP wait cancelled {ClOrdID}; FIX order remains active", id);
        }

        current = get.Execute(new(id));

        return current.IsSuccess
            ? Result<SubmitOrderOutput>.Success(current.Value.ToSubmitOrderOutput())
            : Result<SubmitOrderOutput>.Failure(
                current.Error);
    }
}
