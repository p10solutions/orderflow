namespace OrderGenerator.Application.Repositories;

/// <summary>Durable intents are also the outbox: no terminal result means delivery is pending.</summary>
public interface IOrderIntentRepository
{
    StoredIntent? Find(string id);
    IReadOnlyList<StoredIntent> GetAll();
    int Count();
    IReadOnlyList<StoredIntent> FindPendingDue(DateTimeOffset cutoff, int limit);
    void Save(StoredIntent intent);
}
