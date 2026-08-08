using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Fails every SaveChanges, to stand in for a transient database failure on a write that the
/// caller does not control.
///
/// <para>
/// <b>Why a SaveChanges interceptor reaches the family revoke at all.</b> A reviewer read
/// <c>RevokeAllForUserAsync</c>, saw <c>ExecuteUpdateAsync</c> — which a
/// <see cref="SaveChangesInterceptor"/> cannot intercept — and concluded the injected fault never
/// fires, leaving the tests green while the revoke succeeded. Reasonable from the source, and wrong
/// here: that method branches on <c>IsRelational()</c>, and these tests run on EF InMemory, which
/// takes the OTHER branch (load, mutate, <c>SaveChangesAsync</c>). This interceptor sits exactly
/// there.
/// </para>
/// <para>
/// Verified rather than argued: neutering this class to a no-op turns three of the four
/// fault-injection tests red. The fourth was the one that genuinely could pass without the fault —
/// the reuse branch throws the same <c>AuthenticationException</c> either way — so it now asserts
/// the error log, which only happens inside the catch.
/// </para>
/// <para>
/// <b>What this does NOT cover.</b> Production is relational, so it takes the
/// <c>ExecuteUpdateAsync</c> branch, and no test injects a fault there. That gap is accepted
/// deliberately: the guard is a single <c>catch (Exception ex) when (ex is not
/// OperationCanceledException)</c> wrapping the whole call, so it is agnostic to which branch threw
/// and to the exception's type. Closing the gap would mean a command interceptor matching on SQL
/// text behind a full WebApplicationFactory, to re-prove a branch-agnostic try/catch — cost without
/// assurance. If the guard ever grows a branch-specific case, that trade changes and this needs a
/// relational fixture.
/// </para>
///
/// Used to pin the refresh-token reuse contract: the family revoke is a MITIGATION and the 401 is
/// the CONTRACT, so a revoke that throws must not change the answer the caller gets. The real-world
/// trigger is a deadlock victim or a command timeout on the set-based revoke while concurrent
/// rotations write the same index — timing this repo could observe in CI but not reproduce on
/// demand, which is exactly why the invariant is pinned by injection rather than by racing.
/// </summary>
public sealed class ThrowingSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly Func<Exception> _fault;

    /// <summary>Fails with the default stand-in for a transient database failure.</summary>
    public ThrowingSaveChangesInterceptor()
        : this(Fault) { }

    /// <summary>
    /// Fails with a caller-chosen exception.
    ///
    /// The reuse branch treats one exception type differently from every other — cancellation
    /// propagates, because a caller who hung up is not a failed mitigation — and a fixture that can
    /// only throw one type cannot tell the two apart. ADR-0034 rests on that carve-out, so it needs
    /// to be pinned rather than assumed.
    /// </summary>
    public ThrowingSaveChangesInterceptor(Func<Exception> fault) => _fault = fault;

    /// <summary>The default exception the intercepted context will throw.</summary>
    public static InvalidOperationException Fault() =>
        new("Injected transient database failure (stands in for a deadlock victim).");

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default) => throw _fault();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result) => throw _fault();
}
