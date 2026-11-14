using OrderGenerator.Application.Mapping;
using OrderGenerator.Domain.ValueObjects;
using OrderGenerator.Application.Repositories;

namespace OrderGenerator.Application.UseCases;

public sealed class GetOrderUseCase(IOrderIntentRepository repository, IValidator<GetOrderInput> validator) : IGetOrderUseCase
{
    public Result<GetOrderOutput> Execute(GetOrderInput input)
    {
        var validationError = validator.ValidateInput(input, "OrderNotFound");

        if (validationError is not null)
        {
            return Result<GetOrderOutput>.Failure(validationError);
        }

        if (!OrderIntentId.TryCreate(input.Id, out var identity))
        {
            return Result<GetOrderOutput>.Failure(new("OrderNotFound", "Order identity is unknown"));
        }

        var stored = repository.Find(identity.Value);

        return stored is not null
            ? Result<GetOrderOutput>.Success(stored.ToDomain().ToGetOrderOutput())
            : Result<GetOrderOutput>.Failure(
                new(
                    "OrderNotFound",
                    "Order identity is unknown"));
    }
}
