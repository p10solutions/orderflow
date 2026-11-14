using OrderGenerator.Domain.ValueObjects;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class OrderIntentIdTests
{
    [Fact]
    public void Identity_has_canonical_value_and_case_insensitive_uuid_equality()
    {
        const string id = "f9d4d735-7ea2-4c43-8c84-0d24fe0480b1";
        Assert.Equal(OrderIntentId.Parse(id), OrderIntentId.Parse(id.ToUpperInvariant()));
        Assert.Equal(id, OrderIntentId.Parse(id.ToUpperInvariant()).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("f9d4d7357ea24c438c840d24fe0480b1")]
    public void Invalid_identity_cannot_be_created(string? value)
    {
        Assert.False(OrderIntentId.TryCreate(value, out var id));
        Assert.Null(id);
    }
}
