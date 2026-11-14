using OrderFlow.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using OrderGenerator;
using OrderGenerator.Application.State;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class ResultTests
{
    [Fact]
    public void Success_and_failure_expose_only_their_own_branch()
    {
        var success = Result<int>.Success(42);
        Assert.True(success.IsSuccess);
        Assert.Equal(42, success.Value);
        Assert.Throws<InvalidOperationException>(() => success.Error);
        var error = new Error("ExpectedFailure", "Reason");
        var failure = Result<int>.Failure(error);
        Assert.True(failure.IsFailure);
        Assert.Same(error, failure.Error);
        Assert.Throws<InvalidOperationException>(() => failure.Value);
        Assert.Throws<ArgumentNullException>(() => Result<int>.Failure(null!));
        Assert.Throws<ArgumentNullException>(() => Result<string>.Success(null!));
        Assert.True(Result.Success().IsSuccess);
        Assert.Same(error, Result.Failure(error).Error);
    }

    [Fact]
    public async Task Invalid_submission_returns_field_error_without_registering_or_sending()
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
            "PETR4",
            "Buy",
            1,
            "10.00",
            TimeSpan.Zero), default);
        Assert.True(result.IsFailure);
        Assert.Equal("ValidationFailed", result.Error.Code);
        Assert.True(result.Error.Fields!.ContainsKey("id"));
        Assert.Empty(repository.GetAll());
        Assert.Equal("OrderNotFound", get.Execute(new("invalid")).Error.Code);
        Assert.Equal("OrderNotFound", get.Execute(new(Guid.NewGuid().ToString())).Error.Code);
    }

    private sealed class Transport : IOrderTransport
    {
        public bool Connected => true;

        public bool Send(string id, OrderPayload payload) => throw new InvalidOperationException("Unexpected send");
    }
}
