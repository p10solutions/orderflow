namespace OrderAccumulator.Application.UseCases.ProcessOrder;

public sealed record ProcessOrderInput(
    string Session,
    string ClOrdId,
    string Symbol,
    int Side,
    int Quantity,
    decimal Price,
    string? Fingerprint = null);
