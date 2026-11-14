namespace OrderGenerator.Application.UseCases;

public sealed class GetOrderInputValidator : AbstractValidator<GetOrderInput>
{
    public GetOrderInputValidator() => RuleFor(input => input.Id).NotEmpty().WithMessage("Order identity is required").OverridePropertyName("id");
}
