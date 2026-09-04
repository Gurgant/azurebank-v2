using Microsoft.Extensions.Logging;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Keeps every formatted log line so a test can assert what was — and was not — written. Used where
/// the assertion is about the absence of a value (an address) rather than the presence of a message.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    public List<(LogLevel Level, string Message)> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Recorder(this);

    public void Dispose()
    {
    }

    private sealed class Recorder(RecordingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines)
            {
                owner.Lines.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
