using System.Text.Json;
using Npgsql;
using OrderAccumulator.Application.Repositories;
using OrderAccumulator.Domain;
using static OrderAccumulator.Infrastructure.Persistence.PostgresDatabase;

namespace OrderAccumulator.Infrastructure.Persistence.Repositories;

internal sealed class PostgresDecisionRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : IDecisionRepository
{
    public RecordedDecision? Find(string session, string clOrdId)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT payload,fingerprint,decision FROM decisions WHERE session=@session AND cl_ord_id=@id",
            ("@session", session),
            ("@id", clOrdId));
        using var reader = command.ExecuteReader();

        return reader.Read() ? Decode(reader) : null;
    }

    private static RecordedDecision Decode(NpgsqlDataReader reader)
    {
        var order = JsonSerializer.Deserialize<Order>(reader.GetString(0))!;
        var fingerprint = reader.IsDBNull(1) ? null : reader.GetString(1);
        var decision = JsonSerializer.Deserialize<Decision>(reader.GetString(2))!;

        return new(order, fingerprint, decision);
    }

    public void Add(
        string session,
        string clOrdId,
        RecordedDecision record)
    {
        Execute(
            connection,
            transaction,
            """
            INSERT INTO decisions(session,cl_ord_id,payload,fingerprint,decision,created_utc)
            VALUES(@session,@id,@payload,@fingerprint,@decision,@created)
            """,
            ("@session", session),
            ("@id", clOrdId),
            ("@payload", JsonSerializer.Serialize(record.Order)),
            ("@fingerprint", record.Fingerprint),
            ("@decision", JsonSerializer.Serialize(
                    record.Decision)),
            ("@created", DateTimeOffset.UtcNow.ToString("O")));
    }
}
