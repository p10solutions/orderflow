using Microsoft.Extensions.Logging.Abstractions;
using OrderGenerator;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class OrderRegistryTests
{
    private sealed class Transport : IOrderTransport
    {
        public bool Connected { get; set; } = true;
        public int Sends
        {
            get; private set;
        }
        public Action<string, OrderPayload>? OnSend
        {
            get; set;
        }

        public bool Send(string id, OrderPayload payload)
        {
            Sends++;
            OnSend?.Invoke(id, payload);

            return true;
        }
    }

    private static OrderRequest Request() => new(
        Guid.NewGuid().ToString("D"),
        "PETR4",
        "Buy",
        100,
        "10.01");

    private static TestOrderApplication Registry(Transport transport) => new(
        transport,
        NullLogger<TestOrderApplication>.Instance);

    private static Task<Submission> Submit(
        TestOrderApplication registry,
        OrderRequest request,
        CancellationToken token = default) => registry.Submit(
        request,
        TimeSpan.FromMilliseconds(10),
        token);

    [Fact]
    public async Task R5_Synchronous_report_during_send_is_correlated()
    {
        var transport = new Transport();
        var registry = Registry(transport);
        transport.OnSend = (id, _) => registry.Confirm(new(
            id,
            "Accepted",
            "order",
            "exec"));
        var request = Request();
        var result = await Submit(registry, request);
        Assert.Equal(SubmissionStatus.Confirmed, result.Status);
        Assert.Equal("Accepted", Assert.IsType<OrderResult>(result.Body).Status);
        Assert.Equal("exec", registry.Get(request.Id)!.ExecId);
        Assert.Equal(1, transport.Sends);
    }

    [Fact]
    public async Task R5_Timeout_leaves_queryable_order_and_late_report_updates_it()
    {
        var transport = new Transport();
        var registry = Registry(transport);
        var request = Request();
        var result = await Submit(registry, request);
        Assert.Equal(SubmissionStatus.PendingConfirmation, result.Status);
        Assert.Equal("PendingConfirmation", Assert.IsType<OrderResult>(result.Body).Status);
        Assert.Equal("PendingConfirmation", registry.Get(request.Id)!.Status);
        registry.Confirm(new(
            request.Id,
            "Rejected",
            "order",
            "exec",
            "Exposure limit exceeded"));
        Assert.Equal("Rejected", registry.Get(request.Id)!.Status);
        Assert.Equal("Exposure limit exceeded", registry.Get(request.Id)!.Reason);
        var retry = await Submit(registry, request);
        Assert.Equal(SubmissionStatus.Confirmed, retry.Status);
        Assert.Equal(1, transport.Sends);
    }

    [Fact]
    public async Task R5_Offline_returns_503_without_registering_or_sending()
    {
        var transport = new Transport
        {
            Connected = false
        };
        var registry = Registry(transport);
        var request = Request();
        Assert.Equal(SubmissionStatus.TransportUnavailable, (await Submit(registry, request)).Status);
        Assert.Equal(0, transport.Sends);
        Assert.Null(registry.Get(request.Id));
    }

    [Fact]
    public async Task R5_Equivalent_retry_preserves_identity_and_does_not_send_twice()
    {
        var transport = new Transport();
        var registry = Registry(transport);
        var request = Request();
        Assert.Equal(SubmissionStatus.PendingConfirmation, (await Submit(registry, request)).Status);
        Assert.Equal(
            SubmissionStatus.PendingConfirmation,
            (await Submit(
                registry,
                request with
                {
                    Id = request.Id.ToUpperInvariant(),
                    Price = "10.0100"
                })).Status);
        Assert.Equal(1, transport.Sends);
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("side")]
    [InlineData("quantity")]
    [InlineData("price")]
    public async Task R5_Identity_payload_conflict_returns_409_and_preserves_original(string field)
    {
        var transport = new Transport();
        var registry = Registry(transport);
        var request = Request();
        await Submit(registry, request);
        var changed = field switch
        {
            "symbol" => request with
            {
                Symbol = "VALE3"
            },
            "side" => request with
            {
                Side = "Sell"
            },
            "quantity" => request with
            {
                Quantity = 101
            },
            _ => request with
            {
                Price = "10.02"
            }
        };
        Assert.Equal(SubmissionStatus.IdentityConflict, (await Submit(registry, changed)).Status);
        Assert.Equal(1, transport.Sends);
        registry.Confirm(new(
            request.Id,
            "Accepted",
            "order",
            "exec"));
        Assert.Equal(SubmissionStatus.Confirmed, (await Submit(registry, request)).Status);
    }

    [Fact]
    public async Task R5_Cancelled_http_wait_does_not_cancel_fix_order()
    {
        var transport = new Transport();
        var registry = Registry(transport);
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        transport.OnSend = (_, _) => cancellation.Cancel();
        Assert.Equal(SubmissionStatus.PendingConfirmation, (await Submit(registry, request, cancellation.Token)).Status);
        registry.Confirm(new(
            request.Id,
            "Accepted",
            "order",
            "exec"));
        Assert.Equal("Accepted", registry.Get(request.Id)!.Status);
        Assert.Equal(1, transport.Sends);
    }

    [Fact]
    public async Task R5_Ambiguous_send_exception_is_pending_and_retry_does_not_resend()
    {
        var transport = new Transport
        {
            OnSend = (_, _) => throw new IOException("Disconnected during send")
        };
        var registry = Registry(transport);
        var request = Request();
        Assert.Equal(SubmissionStatus.PendingConfirmation, (await Submit(registry, request)).Status);
        Assert.Equal("PendingConfirmation", registry.Get(request.Id)!.Status);
        Assert.Equal(SubmissionStatus.PendingConfirmation, (await Submit(registry, request)).Status);
        Assert.Equal(1, transport.Sends);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("1000")]
    [InlineData("1000.01")]
    [InlineData("0.001")]
    [InlineData("999.999")]
    [InlineData("10,01")]
    [InlineData("1e2")]
    [InlineData("NaN")]
    [InlineData(" 10.01")]
    [InlineData("10.01 ")]
    [InlineData("")]
    [InlineData("79228162514264337593543950335")]
    [InlineData(null)]
    public async Task R1_Http_invalid_prices_return_400_without_send(string? price)
    {
        var transport = new Transport();
        var result = await Submit(Registry(transport), Request() with
        {
            Price = price!
        });
        Assert.Equal(SubmissionStatus.ValidationFailed, result.Status);
        Assert.Equal(0, transport.Sends);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100000)]
    [InlineData(int.MaxValue)]
    public void R1_Http_quantity_boundaries_are_enforced(int quantity) => Assert.NotNull(
        TestOrderApplication.Validate(
            Request() with
            {
                Quantity = quantity
            },
            out _));

    [Theory]
    [InlineData(1, "0.01")]
    [InlineData(99999, "999.99")]
    [InlineData(1, "10.0100")]
    public void R1_Http_valid_boundaries_preserve_decimal_value(int quantity, string price)
    {
        Assert.Null(
            TestOrderApplication.Validate(Request() with
            {
                Quantity = quantity,
                Price = price
            }, out var payload));
        Assert.Equal(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture), payload.Price);
    }

    [Theory]
    [InlineData("PETR3", "Buy")]
    [InlineData("petr4", "Buy")]
    [InlineData("PETR4", "buy")]
    [InlineData("PETR4", "Other")]
    public void R1_Http_allowlists_are_enforced(string symbol, string side) => Assert.NotNull(
        TestOrderApplication.Validate(
            Request() with
            {
                Symbol = symbol,
                Side = side
            },
            out _));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("816e396081a84cbcA70fd8dd6cc0d349")]
    public void R5_Http_identity_must_be_uuid_in_documented_format(string id) => Assert.NotNull(
        TestOrderApplication.Validate(
            Request() with
            {
                Id = id
            },
            out _));
}
