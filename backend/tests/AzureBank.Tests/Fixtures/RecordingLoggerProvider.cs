using Microsoft.Extensions.Logging;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Keeps every formatted log line — and the text of any exception attached to it, since a console
/// logger prints that too — so a test can assert what was, and was not, written. Used where the
/// assertion is about the absence of a value (an address) rather than the presence of a message.
/// Reads return a snapshot: a hosted loop may still be logging on another thread.
/// </summary>
public sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Message)> _lines = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToArray();
            }
        }
    }

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
            var text = formatter(state, exception);
            if (exception is not null)
            {
                text += " | " + exception;
            }

            lock (owner._lines)
            {
                owner._lines.Add((logLevel, text));
            }
        }
    }
}
