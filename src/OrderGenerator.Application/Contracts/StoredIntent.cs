namespace OrderGenerator;

public sealed record StoredIntent(
    string Id,
    OrderPayload Payload,
    OrderResult? Result,
    string? Diagnostic,
    DateTimeOffset? LastAttempt = null);
