namespace OrderFlow.Shared;

/// <summary>A transport-independent expected application failure.</summary>
public sealed record Error(
    string Code,
    string Message,
    IReadOnlyDictionary<string, string>? Fields = null);
/// <summary>An operation completed successfully, or failed with an expected error.</summary>
public record Result
{
    private readonly Error? error;

    protected Result(Error? error) => this.error = error;

    public bool IsSuccess => error is null;
    public bool IsFailure => !IsSuccess;
    public Error Error => error ?? throw new InvalidOperationException("A successful result has no error.");

    public static Result Success() => new((Error?)null);

    public static Result Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed record Result<T> : Result where T : notnull
{
    private readonly T? value;

    private Result(T value) : base((Error?)null) => this.value = value;

    private Result(Error error) : base(error)
    {
    }

    public T Value => IsSuccess ? value! : throw new InvalidOperationException("A failed result has no value.");

    public static Result<T> Success(T value) => new(value ?? throw new ArgumentNullException(nameof(value)));

    public new static Result<T> Failure(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}
