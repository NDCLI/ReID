using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.App;

public sealed class ObservableLogStore
{
    private const int Capacity = 200;
    private readonly Lock _sync = new();
    private readonly Queue<ObservableLogEntry> _backlog = new(Capacity);

    public event EventHandler<ObservableLogEntry>? MessageAdded;

    public IReadOnlyList<ObservableLogEntry> Snapshot
    {
        get { lock (_sync) { return _backlog.ToArray(); } }
    }

    internal void Add(LogLevel level, string category, string message, Exception? exception)
    {
        var shortCategory = category.Split('.').LastOrDefault() ?? category;
        var details = message;
        if (exception is not null)
        {
            details += $" — {exception.Message}";
        }

        var entry = new ObservableLogEntry(DateTimeOffset.Now, level, shortCategory, details);
        lock (_sync)
        {
            _backlog.Enqueue(entry);
            while (_backlog.Count > Capacity) _backlog.Dequeue();
        }

        MessageAdded?.Invoke(this, entry);
    }
}

public sealed record ObservableLogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public string DisplayText => $"{Timestamp:HH:mm:ss} [{Level}] {Category}: {Message}";
}

public sealed class ObservableLogProvider(ObservableLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ObservableLogger(store, categoryName);
    public void Dispose() { }

    private sealed class ObservableLogger(ObservableLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                store.Add(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
