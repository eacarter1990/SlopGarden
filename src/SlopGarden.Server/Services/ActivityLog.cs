using Microsoft.Extensions.Options;
using SlopGarden.Server.Infrastructure;

namespace SlopGarden.Server.Services;

/// <summary>
/// Plain-text logs on the data volume: one activity log per garden, bed and plant,
/// the server application log, and one client application log per device.
/// </summary>
public sealed class ActivityLog(IOptions<ServerOptions> options)
{
    private readonly Lock _gate = new();
    public const int MaxBytes = 5 * 1024 * 1024;

    public string DirectoryPath => Path.Combine(options.Value.DataDirectory, "logs");
    public string ServerFile => Path.Combine(DirectoryPath, "server.log");

    public void Garden(Guid id, string message) => Append(Path.Combine(DirectoryPath, "gardens", id + ".log"), message);
    public void Bed(Guid id, string message) => Append(Path.Combine(DirectoryPath, "beds", id + ".log"), message);
    public void Plant(Guid id, string message) => Append(Path.Combine(DirectoryPath, "plants", id + ".log"), message);
    public void Server(string message) => Append(ServerFile, message);

    public void Client(Guid deviceId, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var path = Path.Combine(DirectoryPath, "clients", deviceId + ".log");
        lock (_gate)
        {
            try
            {
                Rotate(path);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, text.EndsWith('\n') ? text : text + "\n");
            }
            catch (IOException)
            {
            }
        }
    }

    public void Note(Guid? gardenId, string? targetType, Guid? targetId, string message)
    {
        if (gardenId is { } g) Garden(g, message);
        if (targetType == "bed" && targetId is { } b) Bed(b, message);
        if (targetType == "plant" && targetId is { } p) Plant(p, message);
    }

    public string Read(string scope, string? id, int lines)
    {
        var path = scope switch
        {
            "server" => ServerFile,
            "garden" when Guid.TryParse(id, out var g) => Path.Combine(DirectoryPath, "gardens", g + ".log"),
            "bed" when Guid.TryParse(id, out var b) => Path.Combine(DirectoryPath, "beds", b + ".log"),
            "plant" when Guid.TryParse(id, out var p) => Path.Combine(DirectoryPath, "plants", p + ".log"),
            "client" when Guid.TryParse(id, out var d) => Path.Combine(DirectoryPath, "clients", d + ".log"),
            _ => "",
        };
        if (path.Length == 0 || !File.Exists(path)) return "";
        lines = Math.Clamp(lines, 1, 2000);
        lock (_gate)
        {
            var all = File.ReadAllLines(path);
            return string.Join('\n', all.TakeLast(lines));
        }
    }

    private void Append(string path, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message.Replace('\n', ' ').Replace('\r', ' ')}";
        lock (_gate)
        {
            try
            {
                Rotate(path);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + "\n");
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Rotate(string path)
    {
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length < MaxBytes) return;
        var backup = path + ".1";
        File.Delete(backup);
        File.Move(path, backup);
    }
}

/// <summary>Writes server log events to the persisted server.log alongside the console.</summary>
public sealed class FileLogProvider(string path) : ILoggerProvider
{
    private readonly Lock _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, path, _gate);
    public void Dispose() { }

    private sealed class FileLogger(string category, string path, Lock gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTimeOffset.UtcNow:O} {logLevel} {category}: {formatter(state, exception)}";
            if (exception is not null) line += " " + exception.Message;
            lock (gate)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, line.Replace('\n', ' ') + "\n");
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
