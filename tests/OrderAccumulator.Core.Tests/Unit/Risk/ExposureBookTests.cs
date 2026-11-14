using OrderAccumulator.Domain;
using Xunit;

namespace OrderAccumulator.Domain.Tests;

public sealed class ExposureBookTests
{
    private const string Session = "FIX.4.4:ACCUMULATOR->GENERATOR";

    private static Order Buy(
        int quantity = 1,
        decimal price = .01m,
        string symbol = "PETR4") => new(
        symbol,
        OrderSide.Buy,
        quantity,
        price);

    private static Decision Submit(ExposureBook book, Order order) => book.Process(
        Session,
        Guid.NewGuid().ToString("N"),
        order);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100000)]
    [InlineData(int.MaxValue)]
    public void R1_Invalid_quantity_is_rejected_without_mutation(int quantity)
    {
        var book = new ExposureBook();
        var decision = Submit(book, Buy(quantity));
        Assert.False(decision.Accepted);
        Assert.Equal(0m, book.Exposure("PETR4"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("1000")]
    [InlineData("1000.01")]
    [InlineData("0.001")]
    [InlineData("1.011")]
    [InlineData("79228162514264337593543950335")]
    public void R1_Invalid_price_is_rejected_without_rounding(string text)
    {
        var price = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var book = new ExposureBook();
        Assert.False(Submit(book, Buy(price: price)).Accepted);
        Assert.Equal(0m, book.Exposure("PETR4"));
    }

    [Theory]
    [InlineData(1, "0.01")]
    [InlineData(99999, "999.99")]
    public void R1_Valid_input_boundaries_are_accepted(int quantity, string text)
    {
        var order = Buy(quantity, decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(order.Validate());
        Assert.True(Submit(new ExposureBook(), order).Accepted);
    }

    [Theory]
    [InlineData("PETR3", 1)]
    [InlineData("petr4", 1)]
    [InlineData("", 1)]
    [InlineData("PETR4", 0)]
    [InlineData("PETR4", 3)]
    public void R1_Symbol_and_side_allowlists_are_enforced(string symbol, int side)
    {
        Assert.NotNull(new Order(
            symbol,
            (OrderSide)side,
            1,
            1m).Validate());
    }

    [Fact]
    public void R3_Buy_sell_symbols_and_zero_crossing()
    {
        var book = new ExposureBook();
        Assert.Equal(100m, Submit(book, Buy(10, 10m)).After);
        Assert.Equal(40m, Submit(book, Buy(6, 10m) with
        {
            Side = OrderSide.Sell
        }).After);
        Assert.Equal(-10m, Submit(book, Buy(5, 10m) with
        {
            Side = OrderSide.Sell
        }).After);
        Assert.Equal(0m, Submit(book, Buy(1, 10m)).After);
        Submit(book, Buy(5, 10m, "VALE3"));
        Submit(book, Buy(2, 10m, "VIIA4") with
        {
            Side = OrderSide.Sell
        });
        Assert.Equal(0m, book.Exposure("PETR4"));
        Assert.Equal(50m, book.Exposure("VALE3"));
        Assert.Equal(-20m, book.Exposure("VIIA4"));
    }

    private static void ReachLimit(ExposureBook book, OrderSide side)
    {
        // All orders respect individual form bounds: 99,998,000.01 + 1,999.98 + .01.
        Assert.True(Submit(book, Buy(99999, 999.99m) with
        {
            Side = side
        }).Accepted);
        Assert.True(Submit(book, Buy(2, 999.99m) with
        {
            Side = side
        }).Accepted);
        Assert.True(Submit(book, Buy() with
        {
            Side = side
        }).Accepted);
    }

    [Theory]
    [InlineData(OrderSide.Buy, 1)]
    [InlineData(OrderSide.Sell, -1)]
    public void R3_Inclusive_limit_and_cent_excess_preserve_state(OrderSide side, int sign)
    {
        var book = new ExposureBook();
        ReachLimit(book, side);
        Assert.Equal(sign * ExposureBook.Limit, book.Exposure("PETR4"));
        var rejected = Submit(book, Buy() with
        {
            Side = side
        });
        Assert.False(rejected.Accepted);
        Assert.Equal(rejected.Before, rejected.After);
        Assert.Equal(sign * ExposureBook.Limit, book.Exposure("PETR4"));
        Assert.True(Submit(book, Buy() with
        {
            Side = side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy
        }).Accepted);
        Assert.Equal(sign * (ExposureBook.Limit - .01m), book.Exposure("PETR4"));
    }

    [Fact]
    public void R4_Accepted_duplicate_preserves_ids_and_effect_even_with_equivalent_decimal_scale()
    {
        var book = new ExposureBook();
        var first = book.Process(Session, "same", Buy(10, 10m));
        var duplicate = book.Process(Session, "same", Buy(10, 10.00m));
        Assert.Same(first, duplicate);
        Assert.Equal(100m, book.Exposure("PETR4"));
        Assert.True(book.Process("other-session", "same", Buy(10, 10m)).Accepted);
        Assert.Equal(200m, book.Exposure("PETR4"));
    }

    [Fact]
    public void R4_Rejected_duplicate_stays_rejected_after_capacity_frees()
    {
        var book = new ExposureBook();
        ReachLimit(book, OrderSide.Buy);
        var first = book.Process(Session, "rejected", Buy());
        Assert.False(first.Accepted);
        Submit(book, Buy(1, 1m) with
        {
            Side = OrderSide.Sell
        });
        Assert.Same(first, book.Process(Session, "rejected", Buy()));
        Assert.Equal(ExposureBook.Limit - 1m, book.Exposure("PETR4"));
    }

    [Theory]
    [InlineData("symbol")]
    [InlineData("side")]
    [InlineData("quantity")]
    [InlineData("price")]
    public void R4_Conflicting_payload_does_not_replace_original(string field)
    {
        var book = new ExposureBook();
        var order = Buy(10, 10m);
        var original = book.Process(Session, "same", order);
        var changed = field switch
        {
            "symbol" => order with
            {
                Symbol = "VALE3"
            },
            "side" => order with
            {
                Side = OrderSide.Sell
            },
            "quantity" => order with
            {
                Quantity = 11
            },
            _ => order with
            {
                Price = 10.01m
            }
        };
        var conflict = book.Process(Session, "same", changed);
        Assert.False(conflict.Accepted);
        Assert.Contains("conflict", conflict.Reason);
        Assert.NotEqual(original.ExecId, conflict.ExecId);
        Assert.Same(original, book.Process(Session, "same", order));
        Assert.Equal(100m, book.Exposure("PETR4"));
        Assert.Equal(0m, book.Exposure("VALE3"));
    }

    [Fact]
    public async Task R4_Concurrent_orders_near_limit_have_a_valid_serialization()
    {
        var book = new ExposureBook();
        ReachLimit(book, OrderSide.Buy);
        Submit(book, Buy(1, .01m) with
        {
            Side = OrderSide.Sell
        });
        using var barrier = new Barrier(3);
        Task<Decision> Start(string id) => Task.Factory.StartNew(
            () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));

            return book.Process(Session, id, Buy());
        },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var one = Start("one");
        var two = Start("two");
        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
        var decisions = await Task.WhenAll(one, two);
        Assert.Single(decisions, d => d.Accepted);
        Assert.Single(decisions, d => !d.Accepted);
        Assert.Equal(ExposureBook.Limit, book.Exposure("PETR4"));
    }

    [Fact]
    public async Task R4_Concurrent_duplicates_apply_once()
    {
        var book = new ExposureBook();
        using var barrier = new Barrier(3);
        Task<Decision> Start() => Task.Factory.StartNew(
            () =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));

            return book.Process(Session, "same", Buy(99999, 999.99m));
        },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        var one = Start();
        var two = Start();
        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
        var decisions = await Task.WhenAll(one, two);
        Assert.Same(decisions[0], decisions[1]);
        Assert.Equal(99998000.01m, book.Exposure("PETR4"));
    }

    [Fact]
    public void R3_R4_Seeded_sequences_preserve_signed_sum_of_unique_acceptances()
    {
        var random = new Random(4701);
        var book = new ExposureBook();
        string[] symbols = ["PETR4", "VALE3", "VIIA4"];
        var sums = symbols.ToDictionary(s => s, _ => 0m);
        var previous = new List<(string Id, Order Order, Decision Decision)>();

        for (var i = 0; i < 2000; i++)
        {
            if (previous.Count > 0 && i % 7 == 0)
            {
                var old = previous[random.Next(previous.Count)];
                Assert.Same(old.Decision, book.Process(Session, old.Id, old.Order));
            }
            else
            {
                var order = new Order(
                    symbols[random.Next(symbols.Length)],
                    random.Next(2) == 0 ? OrderSide.Buy : OrderSide.Sell,
                    random.Next(
                        1,
                        100000),
                    random.Next(
                        1,
                        100000) / 100m);
                var id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var decision = book.Process(Session, id, order);

                if (decision.Accepted)
                {
                    sums[order.Symbol] += order.Price * order.Quantity * (order.Side == OrderSide.Buy ? 1m : -1m);
                }

                previous.Add((id, order, decision));
            }

            foreach (var symbol in symbols)
            {
                Assert.Equal(sums[symbol], book.Exposure(symbol));
                Assert.InRange(book.Exposure(symbol), -ExposureBook.Limit, ExposureBook.Limit);
            }
        }
    }
}
