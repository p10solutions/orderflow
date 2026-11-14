using Microsoft.Extensions.Logging.Abstractions;
using OrderGenerator;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class RegistryFailureTests
{
    private sealed class Transport : IOrderTransport
    {
        public bool Connected => true;

        public int Sends;
        public Action<string>? Sending;

        public bool Send(string id, OrderPayload order)
        {
            Interlocked.Increment(ref Sends);
            Sending?.Invoke(id);

            return false;
        }
    }

    private static OrderRequest Request() => new(
        Guid.NewGuid().ToString("D"),
        "PETR4",
        "Buy",
        1,
        "0.01");

    [Fact]
    public async Task R6_Capacity_preserves_known_identities_and_does_not_send_new_ones()
    {
        var transport = new Transport();
        var registry = new TestOrderApplication(transport, NullLogger<TestOrderApplication>.Instance);
        transport.Sending = id => registry.Confirm(new(
            id,
            "Accepted",
            "order",
            "exec"));
        var first = Request();
        await registry.Submit(first, TimeSpan.Zero, default);

        for (var i = 1; i < TestOrderApplication.Capacity; i++)
        {
            await registry.Submit(Request(), TimeSpan.Zero, default);
        }

        var saturated = await registry.Submit(Request(), TimeSpan.Zero, default);
        Assert.Equal(SubmissionStatus.CapacityReached, saturated.Status);
        Assert.Equal("RegistryCapacityReached", Assert.IsType<OrderError>(saturated.Body).Code);
        Assert.Equal(SubmissionStatus.Confirmed, (await registry.Submit(first, TimeSpan.Zero, default)).Status);
        Assert.Equal(TestOrderApplication.Capacity, transport.Sends);
        Assert.Equal("Accepted", registry.Get(first.Id)!.Status);
    }

    [Fact]
    public async Task R6_Concurrent_pending_retries_and_conflict_send_once_even_when_send_returns_false()
    {
        var transport = new Transport();
        var registry = new TestOrderApplication(transport, NullLogger<TestOrderApplication>.Instance);
        var request = Request();
        using var inSend = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        transport.Sending = _ =>
        {
            inSend.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var first = Task.Run(() => registry.Submit(request, TimeSpan.FromMilliseconds(30), default));
        Assert.True(inSend.Wait(TimeSpan.FromSeconds(10)));
        var retries = Enumerable.Range(0, 20).Select(
            _ => registry.Submit(
                request,
                TimeSpan.FromMilliseconds(30),
                default)).ToArray();
        var conflict = await registry.Submit(request with
        {
            Quantity = 2
        }, TimeSpan.Zero, default);
        Assert.Equal(SubmissionStatus.IdentityConflict, conflict.Status);
        release.Set();
        Assert.Equal(SubmissionStatus.PendingConfirmation, (await first).Status);
        Assert.All(
            await Task.WhenAll(retries),
            result => Assert.Equal(
                SubmissionStatus.PendingConfirmation,
                result.Status));
        Assert.Equal(1, transport.Sends);
        registry.Confirm(new(
            request.Id,
            "Accepted",
            "order",
            "exec"));
        Assert.Equal("Accepted", registry.Get(request.Id)!.Status);
    }

    [Fact]
    public async Task R12_Diagnostics_and_contradictions_do_not_replace_the_original_decision()
    {
        var registry = new TestOrderApplication(new Transport(), NullLogger<TestOrderApplication>.Instance);
        var request = Request();
        await registry.Submit(request, TimeSpan.Zero, default);
        registry.Diagnose(new(request.Id, "Protocol failure"));
        Assert.Equal("PendingConfirmation", registry.Get(request.Id)!.Status);
        Assert.Equal("Protocol failure", registry.Get(request.Id)!.Diagnostic);
        var accepted = new OrderResult(
            request.Id,
            "Accepted",
            "order",
            "exec");
        registry.Confirm(accepted);
        registry.Confirm(accepted);
        Assert.Equal(accepted, registry.Get(request.Id));
        registry.Confirm(new(
            request.Id,
            "Rejected",
            "other",
            "other",
            "conflict"));
        Assert.Equal("Accepted", registry.Get(request.Id)!.Status);
        Assert.Equal("exec", registry.Get(request.Id)!.ExecId);
        Assert.Contains("Contradictory", registry.Get(request.Id)!.Diagnostic);
    }
}
