using System.Globalization;
using System.Text.RegularExpressions;
using OrderGenerator.Domain.ValueObjects;

namespace OrderGenerator.Application.UseCases;

public sealed class SubmitOrderInputValidator : AbstractValidator<SubmitOrderInput>
{
    public SubmitOrderInputValidator()
    {
        RuleFor(order => order.Id).Must(id => OrderIntentId.TryCreate(id, out _)).WithMessage("id must be a UUID").OverridePropertyName("id");
        RuleFor(order => order.Symbol).Must(symbol => symbol is "PETR4" or "VALE3" or "VIIA4").WithMessage("Unsupported symbol").OverridePropertyName("symbol");
        RuleFor(order => order.Side).Must(side => side is "Buy" or "Sell").WithMessage("Unsupported side").OverridePropertyName("side");
        RuleFor(order => order.Quantity).InclusiveBetween(1, 99999).WithMessage("Quantity must be between 1 and 99999").OverridePropertyName("quantity");
        RuleFor(order => order.Price).Must(IsValidPrice).WithMessage("Price must be positive, below 1000 and an exact multiple of 0.01").OverridePropertyName("price");
        RuleFor(input => input.ConfirmationTimeout).Must(
            timeout => timeout == Timeout.InfiniteTimeSpan || timeout >= TimeSpan.Zero && timeout.TotalMilliseconds <= uint.MaxValue - 1).WithMessage("Confirmation timeout is outside the supported range").OverridePropertyName("confirmationTimeout");
    }

    private static bool IsValidPrice(string? value) => value is { Length: <= 32 } && Regex.IsMatch(
        value,
        @"^[0-9]{1,4}(\.[0-9]{1,2}0*)?$",
        RegexOptions.CultureInvariant) && decimal.TryParse(
        value,
        NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture,
        out var price) && OrderPayload.IsValidPrice(
        price);
}
