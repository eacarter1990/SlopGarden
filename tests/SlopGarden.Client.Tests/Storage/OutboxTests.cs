using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SlopGarden.Client.Storage;
using SlopGarden.Client.Tests.TestSupport;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Tests.Storage;

public sealed class OutboxTests : IDisposable
{
    private readonly string _dir = Wait.TempDir("outbox");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));

    private string DbFile => Path.Combine(_dir, "outbox.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Outbox Open(ClientDatabase db) => new(db, _time, NullLogger<Outbox>.Instance);

    private static ReadingSample Sample(Guid channel, int i) =>
        new(channel, new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero).AddSeconds(i), i, i * 10, ReadingQuality.Good);

    [Fact]
    public void Readings_survive_a_restart_until_acknowledged()
    {
        var channel = Guid.NewGuid();
        using (var db = new ClientDatabase(DbFile))
        {
            Open(db).AppendReadings([Sample(channel, 1), Sample(channel, 2), Sample(channel, 3)]);
        }

        using (var db = new ClientDatabase(DbFile))
        {
            var outbox = Open(db);
            Assert.Equal(3, outbox.ReadingCount);
            var batch = outbox.PeekReadings(2);
            Assert.Equal([1.0, 2.0], batch.Select(b => b.Sample.Value));
            Assert.Equal(10, batch[0].Sample.RawValue);
            Assert.Equal(channel, batch[0].Sample.ChannelId);
            Assert.Equal(Sample(channel, 1).Time, batch[0].Sample.Time);

            outbox.DeleteReadings(batch);
            Assert.Equal(1, outbox.ReadingCount);
        }

        using (var db = new ClientDatabase(DbFile))
        {
            var outbox = Open(db);
            Assert.Equal(3.0, Assert.Single(outbox.PeekReadings(10)).Sample.Value);
        }
    }

    [Fact]
    public void Null_raw_values_round_trip()
    {
        using var db = new ClientDatabase(DbFile);
        var outbox = Open(db);
        outbox.AppendReadings([new ReadingSample(Guid.NewGuid(), _time.GetUtcNow(), 1.5, null, ReadingQuality.Suspect)]);
        var r = Assert.Single(outbox.PeekReadings(1)).Sample;
        Assert.Null(r.RawValue);
        Assert.Equal(ReadingQuality.Suspect, r.Quality);
    }

    [Fact]
    public void Buffer_cap_drops_the_oldest_readings()
    {
        using var db = new ClientDatabase(DbFile);
        var outbox = Open(db);
        outbox.MaxRows = 5;
        var channel = Guid.NewGuid();
        outbox.AppendReadings(Enumerable.Range(1, 8).Select(i => Sample(channel, i)).ToList());
        Assert.Equal(5, outbox.ReadingCount);
        Assert.Equal([4.0, 5, 6, 7, 8], outbox.PeekReadings(10).Select(r => r.Sample.Value));
    }

    [Fact]
    public void Deleting_an_acknowledged_batch_keeps_readings_appended_meanwhile()
    {
        using var db = new ClientDatabase(DbFile);
        var outbox = Open(db);
        var channel = Guid.NewGuid();
        outbox.AppendReadings([Sample(channel, 1), Sample(channel, 2)]);
        var batch = outbox.PeekReadings(100);
        outbox.AppendReadings([Sample(channel, 3)]);
        outbox.DeleteReadings(batch);
        Assert.Equal(3.0, Assert.Single(outbox.PeekReadings(100)).Sample.Value);
    }

    [Fact]
    public void Messages_are_kept_in_order_and_survive_a_restart()
    {
        var commandId = Guid.NewGuid();
        using (var db = new ClientDatabase(DbFile))
        {
            var outbox = Open(db);
            outbox.AppendMessage(DeviceHubMethods.AckCommand, new CommandAck(commandId, CommandStatuses.Received, null, _time.GetUtcNow()));
            outbox.AppendMessage(DeviceHubMethods.AckCommand, new CommandAck(commandId, CommandStatuses.Completed, null, _time.GetUtcNow()));
            outbox.AppendMessage(DeviceHubMethods.ReportEvent, new DeviceEventReport(_time.GetUtcNow(), "info", "system", "hello", null, null, null));
        }

        using (var db = new ClientDatabase(DbFile))
        {
            var outbox = Open(db);
            var messages = outbox.PeekMessages(10);
            Assert.Equal([DeviceHubMethods.AckCommand, DeviceHubMethods.AckCommand, DeviceHubMethods.ReportEvent], messages.Select(m => m.Method));
            var ack = JsonSerializer.Deserialize<CommandAck>(messages[1].Payload, SlopJson.Options)!;
            Assert.Equal(commandId, ack.CommandId);
            Assert.Equal(CommandStatuses.Completed, ack.Status);

            outbox.DeleteMessage(messages[0].Id);
            Assert.Equal(2, outbox.MessageCount);
        }
    }

    [Fact]
    public async Task Appending_a_message_wakes_the_uploader()
    {
        using var db = new ClientDatabase(DbFile);
        var outbox = Open(db);
        var wait = outbox.MessagesAvailable.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        outbox.AppendMessage(DeviceHubMethods.ReportEvent, new DeviceEventReport(_time.GetUtcNow(), "info", "system", "x", null, null, null));
        Assert.True(await wait);
    }

    [Fact]
    public void Run_ledger_persists_and_queries_overlapping_runs()
    {
        var actuator = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 6, 10, 6, 0, 0, TimeSpan.Zero);
        using (var db = new ClientDatabase(DbFile))
        {
            var ledger = new SqliteRunLedger(db);
            ledger.Record(new RunRecord(Guid.NewGuid(), actuator, t0, t0.AddMinutes(5), 250));
            ledger.Record(new RunRecord(Guid.NewGuid(), actuator, t0.AddHours(2), t0.AddHours(2).AddMinutes(1), null));
            ledger.Record(new RunRecord(Guid.NewGuid(), Guid.NewGuid(), t0, t0.AddHours(3), null));
        }

        using (var db = new ClientDatabase(DbFile))
        {
            var ledger = new SqliteRunLedger(db);
            Assert.Equal(t0.AddHours(2).AddMinutes(1), ledger.LastEnd(actuator));
            var morning = ledger.Query(actuator, t0.AddMinutes(4), t0.AddHours(1));
            var run = Assert.Single(morning);
            Assert.Equal(250, run.AmountMl);
            Assert.Equal(300, run.DurationSec);
            Assert.Equal(2, ledger.Query(actuator, t0.AddDays(-1), t0.AddDays(1)).Count);
            Assert.Empty(ledger.Query(actuator, t0.AddMinutes(5), t0.AddHours(2)));
            Assert.Null(ledger.LastEnd(Guid.NewGuid()));
        }
    }

    [Fact]
    public void Run_ledger_prunes_runs_older_than_a_week()
    {
        var actuator = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 6, 1, 6, 0, 0, TimeSpan.Zero);
        using var db = new ClientDatabase(DbFile);
        var ledger = new SqliteRunLedger(db);
        ledger.Record(new RunRecord(Guid.NewGuid(), actuator, t0, t0.AddMinutes(1), null));
        ledger.Record(new RunRecord(Guid.NewGuid(), actuator, t0.AddDays(8), t0.AddDays(8).AddMinutes(1), null));
        Assert.Single(ledger.Query(actuator, t0.AddDays(-1), t0.AddDays(10)));
    }
}
