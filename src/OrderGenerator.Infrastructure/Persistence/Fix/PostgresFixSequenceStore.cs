using OrderGenerator.Infrastructure.Fix;
using static OrderGenerator.Infrastructure.Persistence.PostgresDatabase;

namespace OrderGenerator.Infrastructure.Persistence.Fix;

public sealed class PostgresFixSequenceStore(PostgresDatabase database) : IFixSequenceStore
{
    public void Record(ulong sequence, string clOrdId)
    {
        using var connection = database.Open();
        Execute(
            connection,
            null,
            """
            INSERT INTO fix_sequences(sequence,intent_id) VALUES(@sequence,@id)
            ON CONFLICT(sequence) DO UPDATE SET intent_id=excluded.intent_id
            """,
            ("@sequence", sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("@id", clOrdId));
    }

    public IReadOnlyDictionary<ulong, string> ReadMappings()
    {
        using var connection = database.Open();
        using var command = Command(connection, null, "SELECT sequence,intent_id FROM fix_sequences");
        using var reader = command.ExecuteReader();
        var result = new Dictionary<ulong, string>();

        while (reader.Read())
        {
            result.Add(
                ulong.Parse(reader.GetString(0), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(1));
        }

        return result;
    }
}
