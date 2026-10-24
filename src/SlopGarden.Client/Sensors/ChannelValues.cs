using System.Collections.Concurrent;

namespace SlopGarden.Client.Sensors;

public sealed record ChannelValue(double Value, DateTimeOffset Time, short Quality);

/// <summary>Latest calibrated value per channel; safety interlocks evaluate against it.</summary>
public interface IChannelValues
{
    ChannelValue? Get(Guid channelId);
    void Set(Guid channelId, ChannelValue value);
    event Action<Guid, ChannelValue>? Updated;
}

public sealed class ChannelValueStore : IChannelValues
{
    private readonly ConcurrentDictionary<Guid, ChannelValue> _values = new();

    public event Action<Guid, ChannelValue>? Updated;

    public ChannelValue? Get(Guid channelId) => _values.TryGetValue(channelId, out var v) ? v : null;

    public void Set(Guid channelId, ChannelValue value)
    {
        _values[channelId] = value;
        Updated?.Invoke(channelId, value);
    }
}
