namespace OrderGenerator.Infrastructure.Persistence;

public static class OrderGeneratorDatabase
{
    public static PostgresDatabase Initialize(string connectionString)
    {
        var database = new PostgresDatabase(connectionString);
        database.Migrate(
            """
        CREATE TABLE intents(id TEXT PRIMARY KEY, payload TEXT NOT NULL, result TEXT,
            diagnostic TEXT, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
        CREATE INDEX pending_intents ON intents(id) WHERE result IS NULL;
        """,
            """
        CREATE TABLE fix_sequences(sequence TEXT PRIMARY KEY, intent_id TEXT NOT NULL);
        """,
            """
        ALTER TABLE intents ADD COLUMN last_attempt_utc timestamptz;
        CREATE INDEX recovery_due_intents ON intents(last_attempt_utc ASC NULLS FIRST,id)
            WHERE result IS NULL AND diagnostic IS NULL;
        """);

        return database;
    }
}
