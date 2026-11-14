namespace OrderAccumulator.Infrastructure.Persistence;

public static class OrderAccumulatorDatabase
{
    public static PostgresDatabase Initialize(string connectionString)
    {
        var database = new PostgresDatabase(connectionString);
        database.Migrate("""
        CREATE TABLE exposures(symbol TEXT PRIMARY KEY, cents BIGINT NOT NULL CHECK(abs(cents) <= 10000000000));
        CREATE TABLE decisions(
            session TEXT NOT NULL, cl_ord_id TEXT NOT NULL, payload TEXT NOT NULL,
            fingerprint TEXT, decision TEXT NOT NULL, created_utc TEXT NOT NULL,
            PRIMARY KEY(session, cl_ord_id));
        """);

        return database;
    }
}
