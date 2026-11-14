using Npgsql;
using OrderAccumulator.Application.Repositories;
using static OrderAccumulator.Infrastructure.Persistence.PostgresDatabase;

namespace OrderAccumulator.Infrastructure.Persistence.Repositories;

internal sealed class PostgresExposureRepository(NpgsqlConnection connection, NpgsqlTransaction transaction) : IExposureRepository
{
    public decimal Get(string symbol)
    {
        using var command = Command(
            connection,
            transaction,
            "SELECT cents FROM exposures WHERE symbol=@symbol",
            ("@symbol", symbol));

        return Convert.ToInt64(command.ExecuteScalar() ?? 0L) / 100m;
    }

    public void Set(string symbol, decimal exposure) => Execute(
        connection,
        transaction,
        """
        INSERT INTO exposures(symbol,cents) VALUES(@symbol,@cents)
        ON CONFLICT(symbol) DO UPDATE SET cents=excluded.cents
        """,
        ("@symbol", symbol),
        ("@cents", checked((long)(exposure * 100m))));
}
