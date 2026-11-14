namespace OrderGenerator.Application.UseCases;

internal abstract record ConfirmationOutcome
{
    internal sealed record Uncorrelated : ConfirmationOutcome;
    internal sealed record Unchanged : ConfirmationOutcome;
    internal sealed record Contradiction : ConfirmationOutcome;
    internal sealed record Confirmed(string Id, TimeSpan Duration) : ConfirmationOutcome;
}
