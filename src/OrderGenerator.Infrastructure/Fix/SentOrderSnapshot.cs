namespace OrderGenerator.Infrastructure.Fix;

internal sealed record SentOrderSnapshot(
    string Symbol,
    char Side,
    decimal Quantity,
    decimal Price);
