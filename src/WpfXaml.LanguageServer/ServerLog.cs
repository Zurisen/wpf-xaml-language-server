namespace WpfXaml.LanguageServer;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Logs to stderr (stdout carries the protocol), to an optional file, and to the client
/// as <c>window/logMessage</c> once a sink is attached.
/// </summary>
public sealed class ServerLog : IDisposable
{
    private readonly LogLevel _minimum;
    private readonly StreamWriter? _file;
    private readonly Lock _gate = new();

    public ServerLog(LogLevel minimum = LogLevel.Info, string? filePath = null)
    {
        _minimum = minimum;
        if (filePath is not null)
        {
            _file = new StreamWriter(filePath, append: true) { AutoFlush = true };
        }
    }

    /// <summary>Forwards messages to the client; set once the JSON-RPC connection exists.</summary>
    public Action<LogLevel, string>? ClientSink { get; set; }

    public void Debug(string message) => Write(LogLevel.Debug, message);

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warning(string message) => Write(LogLevel.Warning, message);

    public void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message}: {exception}");

    private void Write(LogLevel level, string message)
    {
        if (level < _minimum)
        {
            return;
        }

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {level,-7} {message}";
        lock (_gate)
        {
            Console.Error.WriteLine(line);
            _file?.WriteLine(line);
        }

        try
        {
            ClientSink?.Invoke(level, message);
        }
        catch (Exception)
        {
            // The connection may already be gone; logging must never throw.
        }
    }

    public void Dispose() => _file?.Dispose();
}
