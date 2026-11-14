using OrderAccumulator.Domain;

namespace OrderAccumulator.Application.Repositories;

public interface IDecisionRepository
{
    RecordedDecision? Find(string session, string clOrdId);
    void Add(
        string session,
        string clOrdId,
        RecordedDecision record);
}
