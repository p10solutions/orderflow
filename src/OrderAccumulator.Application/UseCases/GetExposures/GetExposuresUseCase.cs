using FluentValidation;
using OrderAccumulator.Application.Repositories;
using OrderAccumulator.Domain;
using OrderFlow.Shared;
using OrderFlow.Shared.Validation;

namespace OrderAccumulator.Application.UseCases.GetExposures;

public sealed record GetExposuresInput;
public sealed record GetExposuresItem(string Symbol, decimal Exposure, decimal Limit);
public sealed record GetExposuresOutput(IReadOnlyList<GetExposuresItem> Items);

public interface IGetExposuresUseCase
{
    Result<GetExposuresOutput> Execute(GetExposuresInput input);
}

public sealed class GetExposuresInputValidator : AbstractValidator<GetExposuresInput>;

public sealed class GetExposuresUseCase(
    IExposureSnapshotRepository repository,
    IValidator<GetExposuresInput> validator) : IGetExposuresUseCase
{
    public Result<GetExposuresOutput> Execute(GetExposuresInput input)
    {
        var error = validator.ValidateInput(input);
        if (error is not null)
        {
            return Result<GetExposuresOutput>.Failure(error);
        }

        var items = repository.Read()
            .Select(item => new GetExposuresItem(item.Symbol, item.Exposure, RiskPolicy.ExposureLimit))
            .ToArray();

        return Result<GetExposuresOutput>.Success(new(items));
    }
}
