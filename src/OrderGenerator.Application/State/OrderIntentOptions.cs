namespace OrderGenerator.Application.State;

public sealed class OrderIntentOptions
{
    public const int DefaultCapacity = 10000;
    public int Capacity
    {
        get;
    }

    public OrderIntentOptions(int capacity = DefaultCapacity)
    {
        if (capacity is < 1 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
    }
}
