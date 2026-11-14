using OrderAccumulator.Application.Repositories;

namespace OrderAccumulator.Infrastructure.Persistence.Repositories;

public sealed class PostgresExposureSnapshotRepository(PostgresDatabase database) : IExposureSnapshotRepository
{
    public IReadOnlyList<ExposureSnapshotItem> Read()
    {
        using var connection = database.Open();
        using var command = PostgresDatabase.Command(connection, null, """
            SELECT assets.symbol, COALESCE(exposures.cents, 0)
            FROM (VALUES ('PETR4'), ('VALE3'), ('VIIA4')) AS assets(symbol)
            LEFT JOIN exposures ON exposures.symbol = assets.symbol
            ORDER BY assets.symbol
            """);
        using var reader = command.ExecuteReader();
        var items = new List<ExposureSnapshotItem>();
        while (reader.Read())
        {
            items.Add(new(reader.GetString(0), reader.GetInt64(1) / 100m));
        }

        return items;
    }
}
