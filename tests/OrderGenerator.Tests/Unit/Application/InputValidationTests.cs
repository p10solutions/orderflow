using Microsoft.Extensions.Logging.Abstractions;
using OrderGenerator;
using OrderGenerator.Application.State;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class InputValidationTests
{
    private sealed class Transport : IOrderTransport
    {
        public bool Connected => true;

        public bool Send(string id, OrderPayload payload) => throw new InvalidOperationException("Invalid input must not send");
    }

    [Fact]
    public async Task Missing_inputs_return_failures_without_persistence_or_transport()
    {
        var repository = new InMemoryOrderIntentRepository();
        var transactions = TestOrderIntentUnitOfWorkFactory.For(repository);
        var notifications = new OrderConfirmationNotifier();
        var get = new GetOrderUseCase(repository, new GetOrderInputValidator());
        var submit = new SubmitOrderUseCase(
            transactions,
            new OrderIntentOptions(),
            notifications,
            new Transport(),
            get,
            NullLogger<SubmitOrderUseCase>.Instance,
            new SubmitOrderInputValidator());
        var confirm = new ConfirmOrderUseCase(
            transactions,
            notifications,
            NullLogger<ConfirmOrderUseCase>.Instance,
            new ConfirmOrderInputValidator());
        var diagnostic = new RecordProtocolDiagnosticUseCase(
            transactions,
            NullLogger<RecordProtocolDiagnosticUseCase>.Instance,
            new RecordProtocolDiagnosticInputValidator());
        var recover = new RecoverPendingOrdersUseCase(
            transactions,
            new Transport(),
            NullLogger<RecoverPendingOrdersUseCase>.Instance,
            new RecoverPendingOrdersInputValidator());
        var known = new GetKnownOrdersUseCase(repository, new GetKnownOrdersInputValidator());
        Assert.True((await submit.ExecuteAsync(null!, default)).IsFailure);
        Assert.True((await submit.ExecuteAsync(new(
            null!,
            null!,
            null!,
            0,
            null!,
            TimeSpan.Zero), default)).IsFailure);
        Assert.True(confirm.Execute(null!).IsFailure);
        Assert.True(confirm.Execute(new(
            null!,
            null!,
            null!,
            null!)).IsFailure);
        Assert.True(diagnostic.Execute(null!).IsFailure);
        Assert.True(diagnostic.Execute(new(Guid.NewGuid().ToString(), null!)).IsFailure);
        Assert.True(get.Execute(null!).IsFailure);
        Assert.True(known.Execute(null!).IsFailure);
        Assert.True(recover.Execute(null!).IsFailure);
        Assert.True(recover.Execute(new(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(-1), 0)).IsFailure);
        Assert.Empty(repository.GetAll());
    }

    [Fact]
    public async Task Multiple_invalid_fields_are_returned_with_stable_field_names_before_admission()
    {
        var repository = new InMemoryOrderIntentRepository();
        var transactions = TestOrderIntentUnitOfWorkFactory.For(repository);
        var notifications = new OrderConfirmationNotifier();
        var get = new GetOrderUseCase(repository, new GetOrderInputValidator());
        var submit = new SubmitOrderUseCase(
            transactions,
            new OrderIntentOptions(),
            notifications,
            new Transport(),
            get,
            NullLogger<SubmitOrderUseCase>.Instance,
            new SubmitOrderInputValidator());
        var result = await submit.ExecuteAsync(new(
            "invalid",
            "OTHER",
            "Other",
            0,
            "0.001",
            TimeSpan.Zero), default);
        Assert.Equal("ValidationFailed", result.Error.Code);

        foreach (var field in new[]
        {
            "id",
            "symbol",
            "side",
            "quantity",
            "price"
        }

        )
        {
            Assert.True(result.Error.Fields!.ContainsKey(field), field);
        }

        Assert.Equal(5, result.Error.Fields!.Count);
        Assert.Empty(repository.GetAll());
        var invalidTimeout = await submit.ExecuteAsync(
            new(
                Guid.NewGuid().ToString(),
                "PETR4",
                "Buy",
                1,
                "10.00",
                TimeSpan.FromSeconds(
                    -2)),
            default);
        Assert.True(invalidTimeout.IsFailure);
        Assert.True(invalidTimeout.Error.Fields!.ContainsKey("confirmationTimeout"));
        Assert.Empty(repository.GetAll());
    }
}
