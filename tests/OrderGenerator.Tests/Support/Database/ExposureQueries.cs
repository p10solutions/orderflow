using OrderAccumulator.Application.Transactions;

namespace OrderGenerator.Tests;

internal static class ExposureQueries
{
    internal static decimal Exposure(this IOrderUnitOfWorkFactory factory, string symbol)
    {
        using var work = factory.Begin();

        return work.Exposures.Get(symbol);
    }
}
