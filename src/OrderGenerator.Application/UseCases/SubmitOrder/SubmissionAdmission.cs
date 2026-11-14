namespace OrderGenerator.Application.UseCases;

internal abstract record SubmissionAdmission
{
    internal sealed record Rejected(Result<SubmitOrderOutput> Result) : SubmissionAdmission;
    internal sealed record Existing : SubmissionAdmission;
    internal sealed record Registered : SubmissionAdmission;
}
