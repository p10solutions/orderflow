namespace OrderGenerator.Application.UseCases;

public sealed record ConfirmOrderInput(
    string Id,
    string Status,
    string? OrderId,
    string? ExecId,
    string? Reason = null);
