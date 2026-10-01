namespace AzureBank.Seeder.Pool;

/// <summary>The exit code a pool run ends with, as a function of what it counted.</summary>
/// <remarks>NOT WRITTEN YET: every run answers 0, so its tests compile and fail.</remarks>
public static class PoolExitCodes
{
    /// <summary>The code <c>recycle</c> exits with.</summary>
    public static int From(PoolRunSummary summary, int lowMark) => 0;

    /// <summary>The code <c>seed-pool</c> exits with.</summary>
    public static int ForSeedPool(PoolRunSummary summary) => 0;

    /// <summary>The name a code is printed with.</summary>
    public static string Name(int exitCode) => string.Empty;
}
