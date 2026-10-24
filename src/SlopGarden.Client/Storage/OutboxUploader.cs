using System.Text.Json;
using SlopGarden.Client.Runtime;
using SlopGarden.Client.Server;
using SlopGarden.Contracts;

namespace SlopGarden.Client.Storage;

/// <summary>Flushes the outbox to the hub while connected: messages promptly, readings every ReadingBatchSeconds.</summary>
public sealed class OutboxUploader(Outbox outbox, IServerLink link, DeviceRuntime runtime, ILogger<OutboxUploader> logger) : BackgroundService
{
    public const int ReadingBatchSize = 1000;
    private const int MessageBatchSize = 200;

    private DateTimeOffset _lastReadingsFlush = DateTimeOffset.MinValue;

    private TimeSpan ReadingInterval => TimeSpan.FromSeconds(Math.Clamp(runtime.Settings.ReadingBatchSeconds, 1, 3600));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var untilReadings = _lastReadingsFlush == DateTimeOffset.MinValue
                    ? TimeSpan.Zero
                    : _lastReadingsFlush + ReadingInterval - DateTimeOffset.UtcNow;
                // While offline only a (re)connect, which signals MessagesAvailable, makes flushing possible.
                var wait = failures > 0 ? TimeSpan.FromSeconds(Math.Min(30, 2 * failures))
                    : !link.IsConnected ? ReadingInterval
                    : TimeSpan.FromMilliseconds(Math.Clamp(untilReadings.TotalMilliseconds, 50, ReadingInterval.TotalMilliseconds));
                await outbox.MessagesAvailable.WaitAsync(wait, stoppingToken);
                if (!link.IsConnected) continue;
                await FlushMessagesAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= _lastReadingsFlush + ReadingInterval)
                {
                    await FlushReadingsAsync(stoppingToken);
                    _lastReadingsFlush = DateTimeOffset.UtcNow;
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                logger.LogWarning("Upload to server failed (will retry): {Error}", ex.Message);
            }
        }
    }

    public async Task FlushMessagesAsync(CancellationToken ct)
    {
        while (link.IsConnected)
        {
            var batch = outbox.PeekMessages(MessageBatchSize);
            if (batch.Count == 0) return;
            foreach (var m in batch)
            {
                using var doc = JsonDocument.Parse(m.Payload);
                await link.InvokeAsync(m.Method, doc.RootElement.Clone(), ct);
                outbox.DeleteMessage(m.Id);
            }
        }
    }

    public async Task FlushReadingsAsync(CancellationToken ct)
    {
        while (link.IsConnected)
        {
            var batch = outbox.PeekReadings(ReadingBatchSize);
            if (batch.Count == 0) return;
            var request = new ReadingBatch(Guid.NewGuid(), batch.Select(r => r.Sample).ToList());
            var ack = await link.InvokeAsync<ReadingBatchAck>(DeviceHubMethods.ReportReadings, request, ct);
            if (ack is null || ack.BatchId != request.BatchId)
                throw new InvalidOperationException($"server acknowledged batch {ack?.BatchId} instead of {request.BatchId}");
            outbox.DeleteReadings(batch);
            if (ack.Accepted < batch.Count)
                logger.LogDebug("Server accepted {Accepted} of {Count} readings (rest rejected as invalid or duplicate)", ack.Accepted, batch.Count);
            if (batch.Count < ReadingBatchSize) return;
        }
    }
}
