using Microsoft.Data.Sqlite;

namespace SlopGarden.Client.Storage;

/// <summary>The client's SQLite database (outbox.db, WAL). A single connection guarded by a lock keeps
/// concurrency trivial; all statements are short.</summary>
public sealed class ClientDatabase : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SqliteConnection _connection;

    public ClientDatabase(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            CREATE TABLE IF NOT EXISTS readings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                channel_id TEXT NOT NULL,
                time_ms INTEGER NOT NULL,
                value REAL NOT NULL,
                raw REAL NULL,
                quality INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                method TEXT NOT NULL,
                payload TEXT NOT NULL,
                created_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS actuator_runs (
                run_id TEXT PRIMARY KEY,
                actuator_id TEXT NOT NULL,
                started_ms INTEGER NOT NULL,
                ended_ms INTEGER NOT NULL,
                amount_ml REAL NULL);
            CREATE INDEX IF NOT EXISTS ix_actuator_runs ON actuator_runs (actuator_id, ended_ms);
            """);
    }

    public T Run<T>(Func<SqliteConnection, T> action)
    {
        lock (_gate) return action(_connection);
    }

    public void Execute(string sql, params (string Name, object? Value)[] parameters) => Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    });

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }
}
