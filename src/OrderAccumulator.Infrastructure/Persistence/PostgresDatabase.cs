using Npgsql;
using NpgsqlTypes;

namespace OrderAccumulator.Infrastructure.Persistence;

public sealed class PostgresDatabase
{
    private readonly string connectionString;

    public PostgresDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = new NpgsqlConnectionStringBuilder(connectionString).ToString();
    }

    public void Migrate(params string[] migrations)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "SELECT pg_advisory_xact_lock(9001)");
        Execute(
            connection,
            transaction,
            "CREATE TABLE IF NOT EXISTS schema_version(id integer PRIMARY KEY CHECK(id=1),version integer NOT NULL)");
        using var version = Command(connection, transaction, "SELECT version FROM schema_version WHERE id=1");
        var current = Convert.ToInt32(version.ExecuteScalar() ?? 0);

        if (current < 0 || current > migrations.Length)
        {
            throw new InvalidOperationException("Unsupported database schema version");
        }

        for (var index = current; index < migrations.Length; index++)
        {
            Execute(connection, transaction, migrations[index]);
            Execute(
                connection,
                transaction,
                "INSERT INTO schema_version(id,version) VALUES(1,@version) ON CONFLICT(id) DO UPDATE SET version=excluded.version",
                ("version", index + 1));
        }

        transaction.Commit();
    }

    public NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(connectionString);

        try
        {
            connection.Open();

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public IDisposable AcquireHostLease()
    {
        var connection = Open();

        try
        {
            using var command = Command(
                connection,
                null,
                "SELECT pg_try_advisory_lock(hashtextextended(current_database()||':'||current_schema()||':host',0))");

            if (!(bool)command.ExecuteScalar()!)
            {
                throw new InvalidOperationException("Another host already owns this database");
            }

            return new HostLease(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private sealed class HostLease(NpgsqlConnection connection) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Execute(
                    connection,
                    null,
                    "SELECT pg_advisory_unlock(hashtextextended(current_database()||':'||current_schema()||':host',0))");
            }
            finally
            {
                connection.Dispose();
            }
        }
    }

    public bool IsAvailable()
    {
        try
        {
            using var connection = Open();
            using var command = Command(connection, null, "SELECT 1");

            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    public static NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            if (value is null)
            {
                command.Parameters.AddWithValue(name.TrimStart('@'), NpgsqlDbType.Text, DBNull.Value);
            }
            else
            {
                command.Parameters.AddWithValue(name.TrimStart('@'), value);
            }
        }

        return command;
    }

    public static void Execute(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(
            connection,
            transaction,
            sql,
            parameters);
        command.ExecuteNonQuery();
    }
}
