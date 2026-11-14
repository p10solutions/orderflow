namespace OrderGenerator;

public sealed record OrderError(
    string Error,
    string Code,
    IReadOnlyDictionary<string, string>? Errors = null);
