using System.Runtime.CompilerServices;
using OrderGenerator;
using OrderGenerator.Application.Repositories;
using OrderGenerator.Application.Transactions;
using OrderGenerator.Infrastructure.Persistence.Transactions;

namespace OrderGenerator.Tests;

/// <summary>In-memory transaction fixture only. Real persistence tests use the PostgreSQL adapter.</summary>
internal sealed class TestOrderIntentUnitOfWorkFactory(IOrderIntentRepository repository) : IOrderIntentUnitOfWorkFactory
{
    private static readonly ConditionalWeakTable<IOrderIntentRepository, object> Gates = new();

    public static IOrderIntentUnitOfWorkFactory For(IOrderIntentRepository repository) => repository is PostgresOrderIntentRepository postgres
        ? new PostgresOrderIntentUnitOfWorkFactory(
            postgres.Database)
        : new TestOrderIntentUnitOfWorkFactory(
            repository);

    public IOrderIntentUnitOfWork BeginIntent(string id) => new Work(repository, Gates.GetValue(repository, _ => new()));

    public IOrderIntentUnitOfWork BeginRecovery() => new Work(repository, Gates.GetValue(repository, _ => new()));

    private sealed class Work : IOrderIntentUnitOfWork, IOrderIntentRepository
    {
        private readonly IOrderIntentRepository repository;
        private readonly object gate;
        private readonly Dictionary<string, StoredIntent> pending = new();

        public Work(IOrderIntentRepository repository, object gate)
        {
            this.repository = repository;
            this.gate = gate;
            Monitor.Enter(gate);
        }

        public IOrderIntentRepository Intents => this;

        public void LockAdmission()
        {
        }

        public StoredIntent? Find(string id) => pending.GetValueOrDefault(id) ?? repository.Find(id);

        public IReadOnlyList<StoredIntent> GetAll() => repository.GetAll().Where(item => !pending.ContainsKey(item.Id)).Concat(
            pending.Values).ToArray();

        public int Count() => repository.Count() + pending.Keys.Count(id => repository.Find(id) is null);

        public IReadOnlyList<StoredIntent> FindPendingDue(DateTimeOffset cutoff, int limit) => repository.FindPendingDue(
            cutoff,
            limit);

        public void Save(StoredIntent intent) => pending[intent.Id] = intent;

        public void Commit()
        {
            foreach (var intent in pending.Values)
            {
                repository.Save(intent);
            }

            pending.Clear();
        }

        public void Dispose() => Monitor.Exit(gate);
    }
}
