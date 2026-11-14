using OrderGenerator.Application;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class GetExposuresUseCaseTests
{
    private sealed class Transport(IReadOnlyList<ExposureTransportItem>? snapshot) : IExposureTransport
    {
        public int Calls
        {
            get; private set;
        }

        public Task<IReadOnlyList<ExposureTransportItem>?> QueryAsync(CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(snapshot);
        }
    }

    [Fact]
    public async Task Unavailable_snapshot_is_failure_and_never_a_zero_balance()
    {
        var transport = new Transport(null);
        var useCase = new GetExposuresUseCase(transport, new GetExposuresInputValidator());
        var result = await useCase.ExecuteAsync(new(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ExposureUnavailable", result.Error.Code);
    }

    [Fact]
    public async Task Signed_snapshot_is_preserved_without_recalculating_orders()
    {
        var transport = new Transport([new("PETR4", -20m), new("VALE3", 100_000_000m), new("VIIA4", 0m)]);
        var useCase = new GetExposuresUseCase(transport, new GetExposuresInputValidator());
        var result = await useCase.ExecuteAsync(new(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(-20m, result.Value.Items[0].Exposure);
        Assert.All(result.Value.Items, item => Assert.Equal(100_000_000m, item.Limit));
    }

    [Fact]
    public async Task Missing_input_does_not_query_transport()
    {
        var transport = new Transport(null);
        var useCase = new GetExposuresUseCase(transport, new GetExposuresInputValidator());
        var result = await useCase.ExecuteAsync(null!, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(0, transport.Calls);
    }
}
