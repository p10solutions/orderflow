using OrderGenerator.Domain.ValueObjects;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class OrderConfirmationTests
{
    [Fact]
    public void Confirmation_is_compared_by_business_value()
    {
        var first = OrderConfirmation.Create(
            ConfirmationStatus.Accepted,
            "order",
            "exec",
            "Accepted");
        Assert.Equal(first, OrderConfirmation.Create(
            ConfirmationStatus.Accepted,
            "order",
            "exec",
            "Accepted"));
        Assert.NotEqual(first, OrderConfirmation.Create(
            ConfirmationStatus.Accepted,
            "order",
            "other",
            "Accepted"));
    }

    [Theory]
    [InlineData(null, "exec")]
    [InlineData("order", null)]
    [InlineData("", "exec")]
    public void Terminal_confirmation_requires_identifiers(string? orderId, string? execId) => Assert.ThrowsAny<ArgumentException>(
        () => OrderConfirmation.Create(
            ConfirmationStatus.Accepted,
            orderId,
            execId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Rejection_requires_reason(string? reason) => Assert.ThrowsAny<ArgumentException>(
        () => OrderConfirmation.Create(
            ConfirmationStatus.Rejected,
            "order",
            "exec",
            reason));

    [Fact]
    public void Undefined_status_cannot_be_a_confirmation() => Assert.Throws<ArgumentOutOfRangeException>(
        () => OrderConfirmation.Create(
            (ConfirmationStatus)99,
            "order",
            "exec"));
}
