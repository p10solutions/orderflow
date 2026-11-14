namespace OrderGenerator.Application.UseCases;

public sealed class RecoverPendingOrdersInputValidator : AbstractValidator<RecoverPendingOrdersInput>
{
    public RecoverPendingOrdersInputValidator()
    {
        RuleFor(input => input.RetryInterval).GreaterThanOrEqualTo(TimeSpan.Zero).WithMessage("Retry interval cannot be negative").OverridePropertyName("retryInterval");
        RuleFor(input => input.BatchSize).GreaterThan(0).WithMessage("Batch size must be positive").OverridePropertyName("batchSize");
    }
}
