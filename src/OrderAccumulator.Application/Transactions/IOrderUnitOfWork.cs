using OrderAccumulator.Application.Repositories;

namespace OrderAccumulator.Application.Transactions;

public interface IOrderUnitOfWork : IDisposable
{
    IDecisionRepository Decisions
    {
        get;
    }

    IExposureRepository Exposures
    {
        get;
    }

    void Commit();
}
