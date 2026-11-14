namespace OrderGenerator.Application.UseCases;

public interface IGetOrderUseCase
{
    Result<GetOrderOutput> Execute(GetOrderInput input);
}
