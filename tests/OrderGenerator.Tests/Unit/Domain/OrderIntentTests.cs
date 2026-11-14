using OrderGenerator;
using OrderGenerator.Domain.Orders;
using OrderGenerator.Domain.ValueObjects;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class OrderIntentTests
{
    private static OrderIntent Pending() => OrderIntent.Pending(
        OrderIntentId.Parse(Guid.NewGuid().ToString("D")),
        new(
            "PETR4",
            "Buy",
            1,
            10m));

    private static OrderConfirmation Confirmation(ConfirmationStatus status) => OrderConfirmation.Create(
        status,
        "order",
        "exec",
        "reason");

    [Theory]
    [InlineData(ConfirmationStatus.Accepted)]
    [InlineData(ConfirmationStatus.Rejected)]
    public void First_confirmation_clears_diagnostic_without_mutating_pending_intention(ConfirmationStatus status)
    {
        var original = Pending().RecordDiagnostic("Transient protocol diagnostic");
        var transition = original.Confirm(Confirmation(status));
        Assert.Equal(ConfirmationChange.Confirmed, transition.Change);
        Assert.False(transition.Next.IsPending);
        Assert.Null(transition.Next.Diagnostic);
        Assert.Equal(status, transition.Next.Confirmation!.Status);
        Assert.Equal(original.Id, transition.Next.Id);
        Assert.Equal(original.Payload, transition.Next.Payload);
        Assert.True(original.IsPending);
        Assert.Equal("Transient protocol diagnostic", original.Diagnostic);
    }

    [Theory]
    [InlineData(ConfirmationStatus.Accepted)]
    [InlineData(ConfirmationStatus.Rejected)]
    public void Equivalent_replay_is_a_noop_and_preserves_diagnostic(ConfirmationStatus status)
    {
        var original = Pending().Confirm(Confirmation(status)).Next.RecordDiagnostic("Existing diagnostic");
        var transition = original.Confirm(Confirmation(status));
        Assert.Equal(ConfirmationChange.EquivalentDuplicate, transition.Change);
        Assert.Same(original, transition.Next);
        Assert.Equal("Existing diagnostic", transition.Next.Diagnostic);
    }

    [Theory]
    [InlineData(ConfirmationStatus.Accepted, ConfirmationStatus.Rejected)]
    [InlineData(ConfirmationStatus.Rejected, ConfirmationStatus.Accepted)]
    public void Contradiction_preserves_original_decision_in_a_new_snapshot(
        ConfirmationStatus first,
        ConfirmationStatus second)
    {
        var original = Pending().Confirm(Confirmation(first)).Next;
        var transition = original.Confirm(Confirmation(second));
        Assert.Equal(ConfirmationChange.Contradiction, transition.Change);
        Assert.Same(original.Confirmation, transition.Next.Confirmation);
        Assert.Equal(OrderIntent.ContradictoryReportDiagnostic, transition.Next.Diagnostic);
        Assert.Null(original.Diagnostic);
    }

    [Fact]
    public void Only_pending_intention_without_diagnostic_can_retry()
    {
        var pending = Pending();
        Assert.True(pending.CanRetry);
        Assert.False(pending.RecordDiagnostic("Protocol rejection").CanRetry);
        Assert.False(pending.Confirm(Confirmation(ConfirmationStatus.Accepted)).Next.CanRetry);
    }

    [Fact]
    public void Payload_matching_uses_value_equality()
    {
        var intent = Pending();
        Assert.True(intent.Matches(new(
            "PETR4",
            "Buy",
            1,
            10.00m)));
        Assert.False(intent.Matches(intent.Payload with
        {
            Quantity = 2
        }));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    public void Invalid_payload_cannot_create_intention(int quantity, int price) => Assert.Throws<ArgumentException>(
        () => OrderIntent.Pending(
            OrderIntentId.Parse(
                Guid.NewGuid().ToString("D")),
            new OrderPayload(
                "PETR4",
                "Buy",
                quantity,
                price)));
}
