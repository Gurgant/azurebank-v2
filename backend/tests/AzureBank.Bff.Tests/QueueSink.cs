using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace AzureBank.Bff.Tests;

/// <summary>
/// A Serilog sink that keeps every event it is given in the queue it was built with, so a test
/// reads what the host logged as values. A test registers it as an <see cref="ILogEventSink"/> in
/// the host's services, which the host's <c>ReadFrom.Services</c> picks up.
/// </summary>
internal sealed class QueueSink(ConcurrentQueue<LogEvent> events) : ILogEventSink
{
    public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
}
