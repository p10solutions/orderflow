using System.Text.Json;
using Npgsql;
using OrderGenerator.Application.Repositories;
using static OrderGenerator.Infrastructure.Persistence.PostgresDatabase;

namespace OrderGenerator.Infrastructure.Persistence.Repositories;

public sealed class PostgresOrderIntentRepository : IOrderIntentRepository
{
    public PostgresDatabase Database
    {
        get;
    }

    private readonly NpgsqlConnection? connection;
    private readonly NpgsqlTransaction? transaction;
    private const string Columns = "id,payload,result,diagnostic,last_attempt_utc";

    public PostgresOrderIntentRepository(PostgresDatabase database) => Database = database;

    internal PostgresOrderIntentRepository(
        PostgresDatabase database,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        Database = database;
        this.connection = connection;
        this.transaction = transaction;
    }

    private T WithConnection<T>(Func<NpgsqlConnection, NpgsqlTransaction?, T> query)
    {
        if (connection is not null)
        {
            return query(connection, transaction);
        }

        using var opened = Database.Open();

        return query(opened, null);
    }

    private static IReadOnlyList<StoredIntent> ReadIntents(NpgsqlDataReader reader)
    {
        var intents = new List<StoredIntent>();

        while (reader.Read())
        {
            intents.Add(Decode(reader));
        }

        return intents;
    }

    private static StoredIntent Decode(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        JsonSerializer.Deserialize<OrderPayload>(
            reader.GetString(1))!,
        reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<OrderResult>(
            reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));

    public IReadOnlyList<StoredIntent> GetAll() => WithConnection<IReadOnlyList<StoredIntent>>(
        (connection, transaction) =>
    {
        using var command = Command(connection, transaction, $"SELECT {Columns} FROM intents ORDER BY created_utc,id");
        using var reader = command.ExecuteReader();

        return ReadIntents(reader);
    });

    public StoredIntent? Find(string id) => WithConnection(
        (connection, transaction) =>
    {
        using var command = Command(
            connection,
            transaction,
            $"SELECT {Columns} FROM intents WHERE id=@id" + (transaction is null ? "" : " FOR UPDATE"),
            ("id", id));
        using var reader = command.ExecuteReader();

        return reader.Read() ? Decode(reader) : null;
    });

    public int Count() => WithConnection(
        (connection, transaction) =>
    {
        using var command = Command(connection, transaction, "SELECT count(*) FROM intents");

        return Convert.ToInt32(command.ExecuteScalar());
    });

    public IReadOnlyList<StoredIntent> FindPendingDue(DateTimeOffset cutoff, int limit) => WithConnection<IReadOnlyList<StoredIntent>>(
        (
        connection,
        transaction) =>
    {
        if (transaction is null)
        {
            throw new InvalidOperationException("Recovery selection requires a unit of work");
        }

        using var command = Command(
            connection,
            transaction,
            $"""
            SELECT {Columns} FROM intents
            WHERE result IS NULL AND diagnostic IS NULL AND (last_attempt_utc IS NULL OR last_attempt_utc<=@cutoff)
            ORDER BY last_attempt_utc NULLS FIRST,id LIMIT @limit FOR UPDATE SKIP LOCKED
            """,
            ("cutoff", cutoff.ToUniversalTime()),
            ("limit", limit));
        using var reader = command.ExecuteReader();

        return ReadIntents(reader);
    });

    public void Save(StoredIntent intent)
    {
        if (connection is not null)
        {
            Write(connection, transaction!, intent);

            return;
        }

        using var opened = Database.Open();
        using var work = opened.BeginTransaction();
        Write(opened, work, intent);
        work.Commit();
    }

    private static void Write(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StoredIntent intent) => Execute(
        connection,
        transaction,
        """
            INSERT INTO intents(id,payload,result,diagnostic,last_attempt_utc,created_utc,updated_utc)
            VALUES(@id,@payload,@result,@diagnostic,CAST(@attempt AS timestamptz),@now,@now)
            ON CONFLICT(id) DO UPDATE SET result=excluded.result,diagnostic=excluded.diagnostic,
                last_attempt_utc=excluded.last_attempt_utc,updated_utc=excluded.updated_utc
            """,
        ("id", intent.Id),
        ("payload", JsonSerializer.Serialize(intent.Payload)),
        ("result", intent.Result is null ? null : JsonSerializer.Serialize(
                    intent.Result)),
        ("diagnostic", intent.Diagnostic),
        ("attempt", intent.LastAttempt?.ToUniversalTime()),
        ("now", DateTimeOffset.UtcNow.ToString("O")));
}
