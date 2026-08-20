using Microsoft.Extensions.Logging;

namespace AutoMarkerReID.App;

public sealed class ObservableLogStore
{
    public event EventHandler<string>? MessageAdded;

    internal void Add(LogLevel level, string category, string message, Exception? exception)
    {
        var shortCategory = category.Split('.').LastOrDefault() ?? category;
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {shortCategory}: {message}";
        if (exception is not null)
        {
            line += $" — {exception.Message}";
        }

        MessageAdded?.Invoke(this, line);
    }
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
