namespace AzureBank.Seeder.Pool;

/// <summary>The exit code a pool run ends with, as a function of what it counted.</summary>
/// <remarks>
/// NOT ALL WRITTEN YET: <see cref="ForSeedPool"/> is; <see cref="From"/> answers 0 and
/// <see cref="Name"/> answers nothing, so their tests compile and fail.
/// </remarks>
public static class PoolExitCodes
{
    /// <summary>Nothing below applies.</summary>
    public const int PoolOk = 0;

    /// <summary>A copy could not be built, and the pool ended below its target.</summary>
    public const int TopUpIncomplete = 12;

    /// <summary>A user that belongs to no copy exists.</summary>
    public const int ForeignUsers = 13;

    /// <summary>The code <c>recycle</c> exits with.</summary>
    public static int From(PoolRunSummary summary, int lowMark) => 0;

    /// <summary>The code <c>seed-pool</c> exits with.</summary>
    /// <remarks>
    /// Only what the run itself did or found: a user outside every copy, else a top-up that came
    /// up short. A pool that was low or empty when the run started is why <c>seed-pool</c> was
    /// run, not a signal, and a one-shot service that exits non-zero stops the stack waiting for
    /// it. A copy that failed while the target was still reached is not a signal either.
    /// </remarks>
    public static int ForSeedPool(PoolRunSummary summary)
    {
        if (summary.ForeignUsers > 0)
        {
            return ForeignUsers;
        }

        return summary.BuildFailed > 0 && summary.Free < summary.Target ? TopUpIncomplete : PoolOk;
    }

    /// <summary>The name a code is printed with.</summary>
    public static string Name(int exitCode) => string.Empty;
}
