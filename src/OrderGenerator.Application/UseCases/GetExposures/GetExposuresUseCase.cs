namespace OrderGenerator.Application.UseCases;

public sealed record GetExposuresInput;
public sealed record GetExposuresItem(string Symbol, decimal Exposure, decimal Limit);
public sealed record GetExposuresOutput(IReadOnlyList<GetExposuresItem> Items);

public interface IGetExposuresUseCase
{
    Task<Result<GetExposuresOutput>> ExecuteAsync(GetExposuresInput input, CancellationToken cancellationToken);
}

public sealed class GetExposuresInputValidator : AbstractValidator<GetExposuresInput>;

public sealed class GetExposuresUseCase(
    IExposureTransport transport,
    IValidator<GetExposuresInput> validator) : IGetExposuresUseCase
{
    public async Task<Result<GetExposuresOutput>> ExecuteAsync(
        GetExposuresInput input,
        CancellationToken cancellationToken)
    {
        var error = validator.ValidateInput(input);
        if (error is not null)
        {
            return Result<GetExposuresOutput>.Failure(error);
        }

        var snapshot = await transport.QueryAsync(cancellationToken);
        if (snapshot is null)
        {
            return Result<GetExposuresOutput>.Failure(new("ExposureUnavailable", "Exposure snapshot is unavailable"));
        }

        var items = snapshot.Select(item => new GetExposuresItem(item.Symbol, item.Exposure, 100_000_000m)).ToArray();

        return Result<GetExposuresOutput>.Success(new(items));
    }
}
