using Npgsql;
using OrderAccumulator.Application.Repositories;
using OrderAccumulator.Application.Transactions;
using OrderAccumulator.Infrastructure.Persistence.Repositories;
using static OrderAccumulator.Infrastructure.Persistence.PostgresDatabase;

namespace OrderAccumulator.Infrastructure.Persistence.Transactions;

public sealed class PostgresOrderUnitOfWorkFactory : IOrderUnitOfWorkFactory
{
    public PostgresDatabase Database
    {
        get;
    }

    public PostgresOrderUnitOfWorkFactory(PostgresDatabase database) => Database = database;

    public IOrderUnitOfWork Begin()
    {
        var connection = Database.Open();
        NpgsqlTransaction? transaction = null;

        try
        {
            transaction = connection.BeginTransaction();
            Execute(connection, transaction, "SELECT pg_advisory_xact_lock(9003)");

            return new UnitOfWork(connection, transaction);
        }
        catch
        {
            transaction?.Dispose();
            connection.Dispose();
            throw;
        }
    }

    private sealed class UnitOfWork : IOrderUnitOfWork
    {
        private readonly NpgsqlConnection connection;
        private readonly NpgsqlTransaction transaction;
        public IDecisionRepository Decisions
        {
            get;
        }
        public IExposureRepository Exposures
        {
            get;
        }

        public UnitOfWork(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            this.connection = connection;
            this.transaction = transaction;
            Decisions = new PostgresDecisionRepository(connection, transaction);
            Exposures = new PostgresExposureRepository(connection, transaction);
        }

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
