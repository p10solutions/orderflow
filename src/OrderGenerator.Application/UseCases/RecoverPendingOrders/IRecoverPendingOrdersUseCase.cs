namespace OrderGenerator.Application.UseCases;

public interface IRecoverPendingOrdersUseCase
{
    Result<RecoverPendingOrdersOutput> Execute(RecoverPendingOrdersInput input);
}
