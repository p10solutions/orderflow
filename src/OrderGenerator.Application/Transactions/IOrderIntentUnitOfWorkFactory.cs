namespace OrderGenerator.Application.Transactions;

public interface IOrderIntentUnitOfWorkFactory
{
    IOrderIntentUnitOfWork BeginIntent(string id);
    IOrderIntentUnitOfWork BeginRecovery();
}
