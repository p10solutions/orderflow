namespace OrderGenerator.Application.UseCases;

public sealed class RecordProtocolDiagnosticInputValidator : AbstractValidator<RecordProtocolDiagnosticInput>
{
    public RecordProtocolDiagnosticInputValidator()
    {
        RuleFor(input => input.Id).NotEmpty().WithMessage("Order identity is required").OverridePropertyName("id");
        RuleFor(input => input.Message).NotNull().WithMessage("Diagnostic is required").OverridePropertyName("message");
    }
}
