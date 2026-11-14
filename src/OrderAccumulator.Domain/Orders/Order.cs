namespace OrderAccumulator.Domain;

public enum OrderSide
{
    Buy = 1,
    Sell = 2
}

public sealed record Order(
    string Symbol,
    OrderSide Side,
    int Quantity,
    decimal Price)
{
    public string? Validate()
    {
        if (Symbol is not ("PETR4" or "VALE3" or "VIIA4"))
        {
            return "Unsupported symbol";
        }

        if (Side is not (OrderSide.Buy or OrderSide.Sell))
        {
            return "Unsupported side";
        }

        if (Quantity is < 1 or > 99_999)
        {
            return "Quantity must be between 1 and 99999";
        }

        if (Price <= 0m || Price >= 1_000m || Price % .01m != 0m)
        {
            return "Price must be positive, below 1000 and an exact multiple of 0.01";
        }

        return null;
    }
}
