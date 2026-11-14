using OrderGenerator.Domain.ValueObjects;

namespace OrderGenerator.Domain.Orders;

public sealed class OrderIntent
{
    public const string ContradictoryReportDiagnostic = "Contradictory FIX report; original decision preserved";
    public OrderIntentId Id
    {
        get;
    }
    public OrderPayload Payload
    {
        get;
    }
    public OrderConfirmation? Confirmation
    {
        get;
    }
    public string? Diagnostic
    {
        get;
    }
    public bool IsPending => Confirmation is null;
    public bool CanRetry => IsPending && Diagnostic is null;

    private OrderIntent(
        OrderIntentId id,
        OrderPayload payload,
        OrderConfirmation? confirmation,
        string? diagnostic)
    {
        Id = id;
        Payload = payload;
        Confirmation = confirmation;
        Diagnostic = diagnostic;
    }

    public static OrderIntent Pending(OrderIntentId id, OrderPayload payload) => Restore(
        id,
        payload,
        null,
        null);

    public static OrderIntent Restore(
        OrderIntentId id,
        OrderPayload payload,
        OrderConfirmation? confirmation,
        string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(payload);
        var error = payload.ValidateInstrumentAndQuantity(out _);

        if (error is not null)
        {
            throw new ArgumentException(error, nameof(payload));
        }

        if (!OrderPayload.IsValidPrice(payload.Price))
        {
            throw new ArgumentException("Invalid order price", nameof(payload));
        }

        return new(
            id,
            payload,
            confirmation,
            diagnostic);
    }

    public bool Matches(OrderPayload payload) => Payload == payload;

    public ConfirmationTransition Confirm(OrderConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        if (IsPending)
        {
            return new(ConfirmationChange.Confirmed, new(
                Id,
                Payload,
                confirmation,
                null));
        }

        if (Confirmation == confirmation)
        {
            return new(ConfirmationChange.EquivalentDuplicate, this);
        }

        return new(ConfirmationChange.Contradiction, RecordDiagnostic(ContradictoryReportDiagnostic));
    }

    public OrderIntent RecordDiagnostic(string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        return new(
            Id,
            Payload,
            Confirmation,
            diagnostic);
    }
}
