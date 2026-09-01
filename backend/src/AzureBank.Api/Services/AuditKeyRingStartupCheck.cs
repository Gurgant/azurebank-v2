using AzureBank.Infrastructure.Data;

namespace AzureBank.Api.Services;

/// <summary>
/// Resolves <see cref="IAuditChain"/> once at startup so a ring that cannot be built stops the
/// host rather than the first request.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ THE RING USED TO PASS A DEPLOYMENT AND FAIL A LOGIN. <c>ValidateOnStart</c> covers the audit
/// OPTIONS — the two keys are there and long enough — while the ring's own rules live in
/// <c>AuditChain</c>'s constructor, and <c>IAuditChain</c> is registered scoped. Nothing resolved it
/// at startup, so a deployment with a retired key and no <c>Audit:FoundingChainKey</c> came up
/// clean, printed "Now listening on", and threw on the first request that opened a
/// <c>AzureBankDbContext</c>. That is a LOGIN, before any authentication — and by ADR-0044 D1 an
/// audited operation whose audit write fails takes the business action down with it, so past the
/// login the same typo surfaces as failed money movements. An operator who deploys, sees the
/// service come up, and concludes the ring is right is the person this class exists for.
/// </para>
/// <para>
/// ⚠️ IT RESOLVES THE RULE, IT DOES NOT RESTATE IT, and that distinction is the whole design. The
/// obvious alternative is an options validator that checks the ring — and it would put the ring's
/// rules in a SECOND place, which ADR-0044 refused in advance for the reason this repository has
/// paid for repeatedly: "a structural rule enforced in one of the two composition roots is a rule
/// the other does not have", and two copies of a rule drift. There is one definition, in the
/// constructor; this only makes it run earlier. Adding a guard there needs no change here.
/// </para>
/// <para>
/// Cheap and safe to do at startup because the constructor takes <c>IOptions&lt;AuditOptions&gt;</c>
/// and a logger and nothing else: no database, no connection, no query. The scope exists only
/// because the registration is scoped.
/// </para>
/// <para>
/// The exception is not caught. <c>AuditKeyRingException</c> already carries the operator-facing
/// sentence, naming the setting at fault and what to do about it, and catching it here to log a
/// second rendering would be one more place for the two to disagree. The operator verifier answers
/// the same refusal with exit 3 from every verb; this is the API's half of that guarantee.
/// </para>
/// </remarks>
public sealed class AuditKeyRingStartupCheck(IServiceScopeFactory scopes) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<IAuditChain>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
