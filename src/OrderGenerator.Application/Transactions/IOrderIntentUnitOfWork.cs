using OrderGenerator.Application.Repositories;

namespace OrderGenerator.Application.Transactions;

public interface IOrderIntentUnitOfWork : IDisposable
{
    IOrderIntentRepository Intents
    {
        get;
    }

    void LockAdmission();
    void Commit();
}
