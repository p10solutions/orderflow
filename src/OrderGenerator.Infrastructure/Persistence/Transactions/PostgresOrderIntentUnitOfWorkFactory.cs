using Npgsql;
using OrderGenerator.Application.Repositories;
using OrderGenerator.Application.Transactions;
using OrderGenerator.Infrastructure.Persistence.Repositories;
using static OrderGenerator.Infrastructure.Persistence.PostgresDatabase;

namespace OrderGenerator.Infrastructure.Persistence.Transactions;

public sealed class PostgresOrderIntentUnitOfWorkFactory(PostgresDatabase database) : IOrderIntentUnitOfWorkFactory
{
    public IOrderIntentUnitOfWork BeginIntent(string id) => Begin(id);

    public IOrderIntentUnitOfWork BeginRecovery() => Begin(null);

    private IOrderIntentUnitOfWork Begin(string? id)
    {
        var connection = database.Open();
        NpgsqlTransaction? transaction = null;

        try
        {
            transaction = connection.BeginTransaction();

            if (id is not null)
            {
                Execute(
                    connection,
                    transaction,
                    "SELECT pg_advisory_xact_lock(hashtextextended(current_database()||':'||current_schema()||':intent:'||@id,0))",
                    ("id", id));
            }

            return new Work(database, connection, transaction);
        }
        catch
        {
            transaction?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    private sealed class Work : IOrderIntentUnitOfWork
    {
        private readonly NpgsqlConnection connection;
        private readonly NpgsqlTransaction transaction;
        public IOrderIntentRepository Intents
        {
            get;
        }

        public Work(
            PostgresDatabase database,
            NpgsqlConnection connection,
            NpgsqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
            Intents = new PostgresOrderIntentRepository(database, connection, transaction);
        }

        public void LockAdmission() => Execute(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(current_database()||':'||current_schema()||':intent-capacity',0))");

        public void Commit() => transaction.Commit();

        public void Dispose()
        {
            try
            {
                transaction.Dispose();
            }
            finally
            {
                connection.Dispose();
            }
        }
    }
}
