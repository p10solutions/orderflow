namespace OrderGenerator.Application.UseCases;

public sealed record GetKnownOrdersItem(
    string Id,
    string Symbol,
    string Side,
    int Quantity,
    decimal Price);
