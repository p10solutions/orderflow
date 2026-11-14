namespace OrderGenerator.Application.UseCases;

public sealed record SubmitOrderOutput(
    string Id,
    string Status,
    string? OrderId = null,
    string? ExecId = null,
    string? Reason = null,
    string? Diagnostic = null);
