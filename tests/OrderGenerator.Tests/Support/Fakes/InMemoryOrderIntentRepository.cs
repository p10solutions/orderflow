using OrderGenerator;
using OrderGenerator.Application.Repositories;

namespace OrderGenerator.Tests;

internal sealed class InMemoryOrderIntentRepository : IOrderIntentRepository
{
    private readonly Dictionary<string, StoredIntent> intents = new(StringComparer.Ordinal);

    public StoredIntent? Find(string id)
    {
        lock (intents)
        {
            return intents.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<StoredIntent> GetAll()
    {
        lock (intents)
        {
            return intents.Values.ToArray();
        }
    }

    public int Count()
    {
        lock (intents)
        {
            return intents.Count;
        }
    }

    public IReadOnlyList<StoredIntent> FindPendingDue(DateTimeOffset cutoff, int limit)
    {
        lock (intents)
        {
            return intents.Values.Where(
                intent => intent.Result is null && intent.Diagnostic is null && (intent.LastAttempt is null || intent.LastAttempt <= cutoff)).OrderBy(
                intent => intent.LastAttempt).ThenBy(
                intent => intent.Id).Take(
                limit).ToArray();
        }
    }

    public void Save(StoredIntent intent)
    {
        lock (intents)
        {
            intents[intent.Id] = intent;
        }
    }
}
