namespace SlopGarden.Client.Storage;

public sealed record RunRecord(Guid RunId, Guid ActuatorId, DateTimeOffset StartedAt, DateTimeOffset EndedAt, double? AmountMl)
{
    public double DurationSec => Math.Max(0, (EndedAt - StartedAt).TotalSeconds);
}

/// <summary>Local history of actuator runs, used to enforce daily limits and minimum intervals (also offline).</summary>
public interface IRunLedger
{
    void Record(RunRecord run);

    /// <summary>Runs overlapping the UTC interval [from, to).</summary>
    IReadOnlyList<RunRecord> Query(Guid actuatorId, DateTimeOffset from, DateTimeOffset to);

    DateTimeOffset? LastEnd(Guid actuatorId);
}

public sealed class SqliteRunLedger(ClientDatabase db) : IRunLedger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public void Record(RunRecord run)
    {
        db.Execute("INSERT OR REPLACE INTO actuator_runs (run_id, actuator_id, started_ms, ended_ms, amount_ml) VALUES ($id, $a, $s, $e, $m)",
            ("$id", run.RunId.ToString()), ("$a", run.ActuatorId.ToString()),
            ("$s", run.StartedAt.ToUnixTimeMilliseconds()), ("$e", run.EndedAt.ToUnixTimeMilliseconds()), ("$m", run.AmountMl));
        db.Execute("DELETE FROM actuator_runs WHERE ended_ms < $cutoff", ("$cutoff", run.EndedAt.Subtract(Retention).ToUnixTimeMilliseconds()));
    }

    public IReadOnlyList<RunRecord> Query(Guid actuatorId, DateTimeOffset from, DateTimeOffset to) => db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT run_id, started_ms, ended_ms, amount_ml FROM actuator_runs WHERE actuator_id = $a AND ended_ms > $from AND started_ms < $to ORDER BY started_ms";
        cmd.Parameters.AddWithValue("$a", actuatorId.ToString());
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        var list = new List<RunRecord>();
        while (r.Read())
        {
            list.Add(new RunRecord(Guid.Parse(r.GetString(0)), actuatorId,
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)),
                r.IsDBNull(3) ? null : r.GetDouble(3)));
        }
        return (IReadOnlyList<RunRecord>)list;
    });

    public DateTimeOffset? LastEnd(Guid actuatorId) => db.Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(ended_ms) FROM actuator_runs WHERE actuator_id = $a";
        cmd.Parameters.AddWithValue("$a", actuatorId.ToString());
        return cmd.ExecuteScalar() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : (DateTimeOffset?)null;
    });
}

public sealed class InMemoryRunLedger : IRunLedger
{
    private readonly Lock _gate = new();
    private readonly List<RunRecord> _runs = [];

    public void Record(RunRecord run)
    {
        lock (_gate)
        {
            _runs.RemoveAll(r => r.RunId == run.RunId);
            _runs.Add(run);
        }
    }

    public IReadOnlyList<RunRecord> Query(Guid actuatorId, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate) return _runs.Where(r => r.ActuatorId == actuatorId && r.EndedAt > from && r.StartedAt < to).OrderBy(r => r.StartedAt).ToList();
    }

    public DateTimeOffset? LastEnd(Guid actuatorId)
    {
        lock (_gate) return _runs.Where(r => r.ActuatorId == actuatorId).Select(r => (DateTimeOffset?)r.EndedAt).Max();
    }
}
