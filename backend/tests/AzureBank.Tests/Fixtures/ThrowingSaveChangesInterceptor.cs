using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Fails every SaveChanges, to stand in for a transient database failure on a write that the
/// caller does not control.
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
