namespace AzureBank.Seeder.Pool;

/// <summary>Builds free demo copies: what <c>seed-pool</c> runs, and what <c>recycle</c> tops up with.</summary>
/// <remarks>NOT WRITTEN YET: it builds nothing, so its tests compile and fail.</remarks>
public sealed class DemoCopyBuilder
{
    /// <summary>
    /// Tops the pool up to <paramref name="target"/> fresh free copies, or to the configured
    /// target when none is given.
    /// </summary>
    public Task<PoolRunSummary> SeedPoolAsync(int? target = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PoolRunSummary());
}
