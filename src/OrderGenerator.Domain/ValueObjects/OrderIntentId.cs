using System.Diagnostics.CodeAnalysis;

namespace OrderGenerator.Domain.ValueObjects;

public sealed record OrderIntentId
{
    public string Value
    {
        get;
    }

    private OrderIntentId(Guid value) => Value = value.ToString("D");

    public static bool TryCreate(string? value, [NotNullWhen(true)] out OrderIntentId? id)
    {
        id = Guid.TryParseExact(value, "D", out var parsed) ? new(parsed) : null;

        return id is not null;
    }

    public static OrderIntentId Parse(string value) => TryCreate(value, out var id)
        ? id
        : throw new ArgumentException(
            "Order identity must be a UUID in D format",
            nameof(
                value));

    public override string ToString() => Value;
}
