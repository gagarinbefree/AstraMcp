using Microsoft.Extensions.Logging;

namespace AstraMcp.Server;

internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly string logDirectory = directory;
    private readonly object gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            lock (provider.gate)
            {
                Directory.CreateDirectory(provider.logDirectory);
                File.AppendAllText(Path.Combine(provider.logDirectory, $"mcp-{DateTime.UtcNow:yyyyMMdd}-{Environment.ProcessId}.log"),
                    $"{DateTimeOffset.UtcNow:O} [{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : "\n" + exception)}\n");
            }
        }
    }
}
