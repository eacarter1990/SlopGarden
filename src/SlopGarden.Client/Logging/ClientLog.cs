using SlopGarden.Client.Cameras;
using SlopGarden.Client.Server;

namespace SlopGarden.Client.Logging;

/// <summary>The client application log on its data directory. A shipper copies new lines to the server.</summary>
public sealed class ClientLogFile(ClientPaths paths)
{
    private readonly Lock _gate = new();
    private long _shipped;

    public string Path => paths.LogFile;

    public void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, line.Replace('\n', ' ') + "\n");
            }
            catch (IOException)
            {
            }
        }
    }

    public string TakeNew()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return "";
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (_shipped > stream.Length) _shipped = 0;
            if (_shipped == 0 && stream.Length > 16 * 1024) _shipped = stream.Length - 16 * 1024;
            stream.Position = _shipped;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            _shipped = stream.Position;
            return text;
        }
    }
}

public sealed class ClientFileLogProvider(ClientLogFile file) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, file);
    public void Dispose() { }

    private sealed class Logger(string category, ClientLogFile file) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.UtcNow:O} {logLevel} {category}: {formatter(state, exception)}";
            if (exception is not null) line += " " + exception.Message;
            file.Append(line);
        }
    }
}

public sealed class ClientLogShipper(ClientLogFile file, IServerLink link, ServerHttp http, ILogger<ClientLogShipper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!link.IsConnected || link.ServerUrl is null || link.DeviceToken is null) continue;
            var text = file.TakeNew();
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                await http.UploadLogAsync(link.ServerUrl, link.DeviceToken, text, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Client log upload failed");
            }
        }
    }
}
