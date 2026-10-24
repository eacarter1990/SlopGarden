using System.Text.Json;
using Microsoft.Data.Sqlite;
using SlopGarden.Client.Infrastructure;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Storage;

public sealed record OutboxReading(long Id, ReadingSample Sample);

public sealed record OutboxMessage(long Id, string Method, string Payload);

/// <summary>Durable store-and-forward queue for readings and hub messages (runs, events, acks).
/// Rows are deleted only after the server acknowledged them.</summary>
public sealed class Outbox
{
    public const int DefaultMaxRows = 500_000;
    public const int MaxMessages = 50_000;

    private readonly ClientDatabase _db;
    private readonly TimeProvider _time;
    private readonly ILogger<Outbox> _logger;
    private long _readingCount;
    private long _droppedSinceLog;
    private DateTimeOffset _lastDropLog = DateTimeOffset.MinValue;

    public Outbox(ClientDatabase db, TimeProvider time, ILogger<Outbox> logger)
    {
        _db = db;
        _time = time;
        _logger = logger;
        _readingCount = Scalar("SELECT COUNT(*) FROM readings");
    }

    public AsyncSignal MessagesAvailable { get; } = new();

    public int MaxRows { get; set; } = DefaultMaxRows;

    public long ReadingCount => Interlocked.Read(ref _readingCount);

    public long MessageCount => Scalar("SELECT COUNT(*) FROM messages");

    public void AppendReadings(IReadOnlyCollection<ReadingSample> samples)
    {
        if (samples.Count == 0) return;
        _db.Run(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO readings (channel_id, time_ms, value, raw, quality) VALUES ($c, $t, $v, $r, $q)";
            var pc = cmd.Parameters.Add("$c", SqliteType.Text);
            var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
            var pv = cmd.Parameters.Add("$v", SqliteType.Real);
            var pr = cmd.Parameters.Add("$r", SqliteType.Real);
            var pq = cmd.Parameters.Add("$q", SqliteType.Integer);
            foreach (var s in samples)
            {
                pc.Value = s.ChannelId.ToString();
                pt.Value = s.Time.ToUnixTimeMilliseconds();
                pv.Value = s.Value;
                pr.Value = s.RawValue is { } raw ? raw : DBNull.Value;
                pq.Value = s.Quality;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return 0;
        });
        if (Interlocked.Add(ref _readingCount, samples.Count) > MaxRows) TrimReadings();
    }

    private void TrimReadings()
    {
        var excess = ReadingCount - MaxRows;
        if (excess <= 0) return;
        var deleted = _db.Run(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM readings WHERE id IN (SELECT id FROM readings ORDER BY id LIMIT $n)";
            cmd.Parameters.AddWithValue("$n", excess);
            return cmd.ExecuteNonQuery();
        });
        Interlocked.Add(ref _readingCount, -deleted);
        _droppedSinceLog += deleted;
        var now = _time.GetUtcNow();
        if (now - _lastDropLog > TimeSpan.FromMinutes(1))
        {
            _logger.LogWarning("Offline buffer full ({MaxRows} rows); dropped {Dropped} oldest readings", MaxRows, _droppedSinceLog);
            _droppedSinceLog = 0;
            _lastDropLog = now;
        }
    }

    public IReadOnlyList<OutboxReading> PeekReadings(int max) => _db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, channel_id, time_ms, value, raw, quality FROM readings ORDER BY id LIMIT $n";
        cmd.Parameters.AddWithValue("$n", max);
        using var r = cmd.ExecuteReader();
        var list = new List<OutboxReading>();
        while (r.Read())
        {
            list.Add(new OutboxReading(r.GetInt64(0), new ReadingSample(
                Guid.Parse(r.GetString(1)),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                r.GetDouble(3),
                r.IsDBNull(4) ? null : r.GetDouble(4),
                (short)r.GetInt32(5))));
        }
        return (IReadOnlyList<OutboxReading>)list;
    });

    /// <summary>Removes acknowledged readings. Ids are monotonic, so a peeked batch is the id range [first, last].</summary>
    public void DeleteReadings(IReadOnlyList<OutboxReading> batch)
    {
        if (batch.Count == 0) return;
        var deleted = _db.Run(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM readings WHERE id BETWEEN $a AND $b";
            cmd.Parameters.AddWithValue("$a", batch[0].Id);
            cmd.Parameters.AddWithValue("$b", batch[^1].Id);
            return cmd.ExecuteNonQuery();
        });
        Interlocked.Add(ref _readingCount, -deleted);
    }

    public void AppendMessage(string method, object payload)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), ClientJson.Options);
        _db.Execute("INSERT INTO messages (method, payload, created_ms) VALUES ($m, $p, $t)",
            ("$m", method), ("$p", json), ("$t", _time.GetUtcNow().ToUnixTimeMilliseconds()));
        if (MessageCount > MaxMessages)
            _db.Execute("DELETE FROM messages WHERE id IN (SELECT id FROM messages ORDER BY id LIMIT $n)", ("$n", MessageCount - MaxMessages));
        MessagesAvailable.Set();
    }

    public IReadOnlyList<OutboxMessage> PeekMessages(int max) => _db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, method, payload FROM messages ORDER BY id LIMIT $n";
        cmd.Parameters.AddWithValue("$n", max);
        using var r = cmd.ExecuteReader();
        var list = new List<OutboxMessage>();
        while (r.Read()) list.Add(new OutboxMessage(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return (IReadOnlyList<OutboxMessage>)list;
    });

    public void DeleteMessage(long id) => _db.Execute("DELETE FROM messages WHERE id = $id", ("$id", id));

    private long Scalar(string sql) => _db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    });
}
