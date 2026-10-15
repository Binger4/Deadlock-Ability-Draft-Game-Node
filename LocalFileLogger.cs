namespace AbilityDraft.GameNode;

// Daily local logs; native worker stdout/stderr stay in their individual match directories.
public sealed class LocalFileLogger(string directory) : ILoggerProvider
{
    private readonly string directory = directory;
    private readonly object gate = new();
    public ILogger CreateLogger(string categoryName) => new Writer(this, categoryName);
    public void Dispose() { }
    private sealed class Writer(LocalFileLogger owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            lock (owner.gate)
            {
                Directory.CreateDirectory(owner.directory);
                File.AppendAllText(Path.Combine(owner.directory, $"agent-{DateTime.UtcNow:yyyy-MM-dd}.log"),
                    $"{DateTime.UtcNow:O} [{logLevel}] {category}: {formatter(state, exception)}{Environment.NewLine}");
            }
        }
    }
}
