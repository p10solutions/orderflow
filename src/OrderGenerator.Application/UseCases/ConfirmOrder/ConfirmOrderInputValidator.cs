namespace OrderGenerator.Application.UseCases;

public sealed class ConfirmOrderInputValidator : AbstractValidator<ConfirmOrderInput>
{
    public ConfirmOrderInputValidator()
    {
        RuleFor(input => input.Status).Must(status => status is "Accepted" or "Rejected").WithMessage("A confirmation must be terminal").OverridePropertyName("status");
        RuleFor(input => input.OrderId).NotEmpty().WithMessage("OrderID is required").OverridePropertyName("orderId");
        RuleFor(input => input.ExecId).NotEmpty().WithMessage("ExecID is required").OverridePropertyName("execId");
        RuleFor(input => input.Reason).NotEmpty().When(input => input.Status == "Rejected").WithMessage("Rejected confirmation requires a reason").OverridePropertyName("reason");
    }
}
