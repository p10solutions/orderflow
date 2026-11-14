using OrderGenerator.Domain.Orders;
using OrderGenerator.Domain.ValueObjects;

namespace OrderGenerator.Application.Mapping;

internal static class OrderIntentMapping
{
    internal static OrderConfirmation ToConfirmation(this OrderResult result) => OrderConfirmation.Create(
        result.Status switch
        {
            "Accepted" => ConfirmationStatus.Accepted,
            "Rejected" => ConfirmationStatus.Rejected,
            _ => throw new ArgumentException("A confirmation must be terminal", nameof(result))
        },
        result.OrderId,
        result.ExecId,
        result.Reason);

    internal static OrderIntent ToDomain(this StoredIntent stored)
    {
        var id = OrderIntentId.Parse(stored.Id);

        if (stored.Result is not null && OrderIntentId.Parse(stored.Result.Id) != id)
        {
            throw new InvalidDataException("Persisted confirmation does not belong to the order intention");
        }

        return OrderIntent.Restore(
            id,
            stored.Payload,
            stored.Result?.ToConfirmation(),
            stored.Diagnostic);
    }

    internal static OrderResult ToResult(this OrderIntent intent) => intent.Confirmation is { } confirmation
        ? new(
            intent.Id.Value,
            confirmation.Status.ToString(),
            confirmation.OrderId,
            confirmation.ExecId,
            confirmation.Reason,
            intent.Diagnostic)
        : new(
            intent.Id.Value,
            "PendingConfirmation",
            Diagnostic: intent.Diagnostic);

    internal static StoredIntent ToStored(this OrderIntent intent, DateTimeOffset? lastAttempt = null) => new(
        intent.Id.Value,
        intent.Payload,
        intent.IsPending ? null : intent.ToResult() with
        {
            Diagnostic = null
        },
        intent.Diagnostic,
        lastAttempt);
}
