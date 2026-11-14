namespace OrderAccumulator.Application.UseCases.ProcessOrder;

public sealed record ProcessOrderOutput(
    string OrderId,
    string ExecId,
    bool Accepted,
    string Reason,
    decimal Before,
    decimal After);
