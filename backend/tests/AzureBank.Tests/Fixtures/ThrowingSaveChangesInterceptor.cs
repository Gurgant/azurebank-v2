using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Fails every SaveChanges, to stand in for a database that cannot take a write.
///
/// <para>
/// Two uses in <c>RefreshTokenServiceTests</c>, one in each direction. A renewal run on a context
/// carrying this must still succeed, which is the unit-level proof that renewal writes nothing
/// (06 §4.3): had it tried, it would have thrown. A revoke run on one must answer 503, which pins
/// 06 §4.4's "the database failed, so retry".
/// </para>
/// <para>
/// <b>Why a SaveChanges interceptor reaches a revoke at all.</b> The revokes use
/// <c>ExecuteUpdateAsync</c>, which a <see cref="SaveChangesInterceptor"/> cannot intercept — on a
/// relational provider. These tests run on EF InMemory, where the service takes its OTHER branch
/// (load, mutate, <c>SaveChangesAsync</c>), and this interceptor sits exactly there. The relational
/// branch is faulted on SQL Server by <c>RefreshTokenRotationSqlServerTests</c>, through a command
/// interceptor.
/// </para>
/// <para>
/// <i>Until PR-1 this pinned the reuse branch's guarded family revoke (ADR-0034), which the
/// tripwire replaced (06 F3); the caller-chosen-exception constructor went with it.</i>
/// </para>
/// </summary>
public sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <summary>The exception the intercepted context throws.</summary>
    public static InvalidOperationException Fault() =>
        new("Injected transient database failure (stands in for a deadlock victim).");

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default) => throw Fault();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result) => throw Fault();
}
