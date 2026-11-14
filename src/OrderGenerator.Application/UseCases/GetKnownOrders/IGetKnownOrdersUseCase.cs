namespace OrderGenerator.Application.UseCases;

public interface IGetKnownOrdersUseCase
{
    Result<GetKnownOrdersOutput> Execute(GetKnownOrdersInput input);
}
