namespace OrderAccumulator.Application.Transactions;

public interface IOrderUnitOfWorkFactory
{
    IOrderUnitOfWork Begin();
}
