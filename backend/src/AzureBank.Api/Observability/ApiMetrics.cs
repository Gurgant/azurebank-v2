using System.Diagnostics.Metrics;

namespace AzureBank.Api.Observability;

/// <summary>
/// Application-owned domain metrics — RED at the banking-operation level, not just the HTTP edge.
/// http.server.request.duration tells you POST /api/transfers is slow; these tell you how many
/// transfers actually completed and how many logins were rejected.
///
/// Registered with OpenTelemetry via <c>AddMeter(ApiMetrics.MeterName)</c>. Tags are strictly
/// LOW-CARDINALITY outcome labels — never an account id, user id, amount, or free-form text. A
/// money amount, if ever measured, belongs in a histogram VALUE with domain buckets, never a label.
/// </summary>
public static class ApiMetrics
{
    public const string MeterName = "AzureBank.Api";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Login attempts, tagged <c>outcome</c> = succeeded | failed | locked.</summary>
    public static readonly Counter<long> Logins =
        Meter.CreateCounter<long>("azurebank.logins", unit: "{login}", description: "Login attempts by outcome.");

    /// <summary>Completed money transfers, tagged <c>kind</c> = external | internal.</summary>
    public static readonly Counter<long> Transfers =
        Meter.CreateCounter<long>("azurebank.transfers", unit: "{transfer}", description: "Completed transfers by kind.");

    /// <summary>Idempotency replays served — a retry that returned the stored response (ADR-0009).</summary>
    public static readonly Counter<long> IdempotencyReplays =
        Meter.CreateCounter<long>("azurebank.idempotency.replays", unit: "{replay}", description: "Idempotency replays served.");
}
