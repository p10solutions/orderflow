namespace OrderGenerator.Domain.Orders;

public sealed class ConfirmationTransition
{
    public ConfirmationChange Change
    {
        get;
    }
    public OrderIntent Next
    {
        get;
    }

    internal ConfirmationTransition(ConfirmationChange change, OrderIntent next)
    {
        Change = change;
        Next = next;
    }
}
