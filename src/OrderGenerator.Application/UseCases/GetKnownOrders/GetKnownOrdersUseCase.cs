using OrderGenerator.Application.Repositories;
using OrderGenerator.Application.Mapping;

namespace OrderGenerator.Application.UseCases;

public sealed class GetKnownOrdersUseCase(IOrderIntentRepository repository, IValidator<GetKnownOrdersInput> validator) : IGetKnownOrdersUseCase
{
    public Result<GetKnownOrdersOutput> Execute(GetKnownOrdersInput input)
    {
        var validationError = validator.ValidateInput(input, "ValidationFailed");

        if (validationError is not null)
        {
            return Result<GetKnownOrdersOutput>.Failure(validationError);
        }

        return Result<GetKnownOrdersOutput>.Success(
            new(
                repository.GetAll().Select(
                    stored => stored.ToDomain().ToKnownOrderItem()).ToArray()));
    }
}
