namespace OrderAccumulator.Domain;

public sealed record Decision(
    string OrderId,
    string ExecId,
    bool Accepted,
    string Reason,
    decimal Before,
    decimal After)
{
    public static Decision Accept(decimal before, decimal after) => Create(
        true,
        "Accepted",
        before,
        after);

    public static Decision Reject(string reason, decimal exposure) => Create(
        false,
        reason,
        exposure,
        exposure);

    public static Decision RejectPayloadConflict(decimal exposure) => Reject("ClOrdID payload conflict", exposure);

    private static Decision Create(
        bool accepted,
        string reason,
        decimal before,
        decimal after) => new(
        Guid.NewGuid().ToString("N"),
        Guid.NewGuid().ToString("N"),
        accepted,
        reason,
        before,
        after);
}
