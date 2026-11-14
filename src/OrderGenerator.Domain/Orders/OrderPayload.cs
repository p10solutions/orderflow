namespace OrderGenerator;

public sealed record OrderPayload(
    string Symbol,
    string Side,
    int Quantity,
    decimal Price)
{
    public string? ValidateInstrumentAndQuantity(out string field)
    {
        field = "symbol";

        if (Symbol is not ("PETR4" or "VALE3" or "VIIA4"))
        {
            return "Unsupported symbol";
        }

        field = "side";

        if (Side is not ("Buy" or "Sell"))
        {
            return "Unsupported side";
        }

        field = "quantity";

        return Quantity is < 1 or > 99999 ? "Quantity must be between 1 and 99999" : null;
    }

    public static bool IsValidPrice(decimal price) => price > 0m && price < 1000m && price % .01m == 0m;
}
