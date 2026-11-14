namespace OrderGenerator.Application.UseCases;

public interface ISubmitOrderUseCase
{
    Task<Result<SubmitOrderOutput>> ExecuteAsync(SubmitOrderInput input, CancellationToken cancellationToken);
}
