namespace OrderGenerator.Application.UseCases;

public interface IConfirmOrderUseCase
{
    Result<ConfirmOrderOutput> Execute(ConfirmOrderInput input);
}
