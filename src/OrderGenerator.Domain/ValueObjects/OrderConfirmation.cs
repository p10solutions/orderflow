namespace OrderGenerator.Domain.ValueObjects;

public sealed record OrderConfirmation
{
    public ConfirmationStatus Status
    {
        get;
    }
    public string OrderId
    {
        get;
    }
    public string ExecId
    {
        get;
    }
    public string? Reason
    {
        get;
    }

    private OrderConfirmation(
        ConfirmationStatus status,
        string orderId,
        string execId,
        string? reason)
    {
        Status = status;
        OrderId = orderId;
        ExecId = execId;
        Reason = reason;
    }

    public static OrderConfirmation Create(
        ConfirmationStatus status,
        string? orderId,
        string? execId,
        string? reason = null)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(execId);

        if (status == ConfirmationStatus.Rejected)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        }

        return new(
            status,
            orderId,
            execId,
            reason);
    }
}
