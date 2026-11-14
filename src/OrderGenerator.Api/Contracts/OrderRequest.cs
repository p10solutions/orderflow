namespace OrderGenerator.Api;

public sealed record OrderRequest(
    string Id,
    string Symbol,
    string Side,
    int Quantity,
    string Price);
