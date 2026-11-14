namespace OrderAccumulator.Application.UseCases.ProcessOrder;

public sealed class ProcessOrderInputValidator : AbstractValidator<ProcessOrderInput>
{
    public ProcessOrderInputValidator()
    {
        RuleFor(input => input.Session).NotEmpty().WithMessage("FIX session is required").OverridePropertyName("session");
        RuleFor(input => input.ClOrdId).NotEmpty().WithMessage("ClOrdID is required").OverridePropertyName("clOrdId");
        // Financial invalidity must remain a durable, deduplicated rejection inside the transaction.
    }
}
