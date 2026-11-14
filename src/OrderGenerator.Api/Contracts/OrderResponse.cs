namespace OrderGenerator.Api;

public sealed record OrderResponse(
    string Id,
    string Status,
    string? OrderId = null,
    string? ExecId = null,
    string? Reason = null,
    string? Diagnostic = null);
