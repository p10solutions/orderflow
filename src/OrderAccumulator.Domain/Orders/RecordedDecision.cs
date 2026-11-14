namespace OrderAccumulator.Domain;

public sealed record RecordedDecision(
    Order Order,
    string? Fingerprint,
    Decision Decision)
{
    public bool Matches(Order order, string? fingerprint) => Order == order && Fingerprint == fingerprint;
}
