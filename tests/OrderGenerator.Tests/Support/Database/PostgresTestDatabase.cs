using Npgsql;

namespace OrderGenerator.Tests;

internal sealed class PostgresTestDatabase : IDisposable
{
    private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString
    {
        get;
    }

    private readonly string admin;

    public PostgresTestDatabase()
    {
        admin = Environment.GetEnvironmentVariable("ORDERFLOW_TEST_DATABASE") ?? "Host=127.0.0.1;Port=15432;Database=accumulator;Username=orderflow;Password=orderflow-local";
        using var connection = new NpgsqlConnection(admin);
        connection.Open();
        using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
        command.ExecuteNonQuery();
        ConnectionString = new NpgsqlConnectionStringBuilder(admin)
        {
            SearchPath = schema,
            Pooling = false
        }.ToString();
    }

    public void Dispose()
    {
        using var connection = new NpgsqlConnection(admin);
        connection.Open();
        using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
        command.ExecuteNonQuery();
    }
}
