namespace OrderGenerator.Application.UseCases;

public sealed record SubmitOrderInput(
    string Id,
    string Symbol,
    string Side,
    int Quantity,
    string Price,
    TimeSpan ConfirmationTimeout);
