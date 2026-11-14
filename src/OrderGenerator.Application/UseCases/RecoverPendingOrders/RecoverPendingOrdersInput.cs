namespace OrderGenerator.Application.UseCases;

public sealed record RecoverPendingOrdersInput(
    DateTimeOffset Now,
    TimeSpan RetryInterval,
    int BatchSize = 64);
