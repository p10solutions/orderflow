namespace OrderGenerator.Application.UseCases;

public sealed record GetKnownOrdersOutput(IReadOnlyList<GetKnownOrdersItem> Orders);
