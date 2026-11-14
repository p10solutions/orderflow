namespace OrderGenerator.Infrastructure.Fix;

internal abstract record ReportDecodingResult
{
    internal sealed record Valid(OrderResult Result) : ReportDecodingResult;
    internal sealed record Invalid(string Diagnostic) : ReportDecodingResult;
}
