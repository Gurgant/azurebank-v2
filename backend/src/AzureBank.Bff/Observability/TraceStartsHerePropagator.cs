using System.Diagnostics;

namespace AzureBank.Bff.Observability;

/// <summary>
/// Reads no trace context from a request, so the host starts a new trace for each one. What is
/// written through it is written as <paramref name="inner"/> writes it; in this host nothing is:
/// the two clients write through <see cref="DistributedContextPropagator.Current"/>.
/// </summary>
/// <remarks>
/// The framework ships no propagator that does this:
/// <see cref="DistributedContextPropagator.CreateNoOutputPropagator"/> writes nothing and still
/// reads the caller's <c>traceparent</c>: with it in the container an answer carries the trace
/// id the request named.
/// </remarks>
public sealed class TraceStartsHerePropagator(DistributedContextPropagator inner) : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields => inner.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        inner.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(
        object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier, PropagatorGetterCallback? getter) => null;
}
