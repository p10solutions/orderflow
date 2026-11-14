namespace OrderAccumulator.Domain;

public sealed class ExposureBook
{
    public const decimal Limit = RiskPolicy.ExposureLimit;
    private readonly object gate = new();
    private readonly Dictionary<string, decimal> exposures = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Session, string ClOrdId), RecordedDecision> decisions = new();

    public Decision Process(
        string session,
        string clOrdId,
        Order order,
        string? fingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(clOrdId);
        ArgumentNullException.ThrowIfNull(order);

        lock (gate)
        {
            var key = (session, clOrdId);
            var before = exposures.GetValueOrDefault(order.Symbol ?? "");

            if (decisions.TryGetValue(key, out var previous))
            {
                if (previous.Matches(order, fingerprint))
                {
                    return previous.Decision;
                }

                return Decision.RejectPayloadConflict(before);
            }

            var decision = RiskPolicy.Decide(order, before);

            if (decision.Accepted)
            {
                exposures[order.Symbol!] = decision.After;
            }

            decisions.Add(key, new(order, fingerprint, decision));

            return decision;
        }
    }

    public decimal Exposure(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        lock (gate)
        {
            return exposures.GetValueOrDefault(symbol);
        }
    }
}
