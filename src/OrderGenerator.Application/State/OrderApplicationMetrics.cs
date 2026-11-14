using System.Diagnostics.Metrics;

namespace OrderGenerator.Application.State;

internal static class OrderApplicationMetrics
{
    private static readonly Meter Meter = new("OrderFlow.Generator");
    internal static readonly Counter<long> Admitted = Meter.CreateCounter<long>("orderflow.intents.admitted");
    internal static readonly Counter<long> RecoveryAttempts = Meter.CreateCounter<long>("orderflow.recovery.attempts");
    internal static readonly Histogram<double> ConfirmationDuration = Meter.CreateHistogram<double>(
        "orderflow.confirmation.duration",
        "ms");
}
