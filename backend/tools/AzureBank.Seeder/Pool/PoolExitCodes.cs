namespace AzureBank.Seeder.Pool;

/// <summary>The exit code a pool run ends with, as a function of what it counted.</summary>
/// <remarks>
/// <para>
/// A code from 10 to 15 is a SIGNAL: the run finished and has a summary, and the code says which
/// of its counts somebody should read. 10, 11 and 15 mean "done, and the pool was short"; 12, 13
/// and 14 need a look. A run that did not finish has no summary and no code from here.
/// </para>
/// <para>
/// One run can earn several. It exits with the first that applies of 13, 14, 12, 15, 11, 10: what
/// needs a look before what was only short. The summary line carries every count whatever the
/// code.
/// </para>
/// </remarks>
public static class PoolExitCodes
{
    /// <summary>Nothing below applies.</summary>
    public const int PoolOk = 0;

    /// <summary>The run found fewer free copies than the low mark, and at least one.</summary>
    public const int PoolLow = 10;

    /// <summary>The run found no free copy: visitors may have been turned away.</summary>
    public const int PoolEmpty = 11;

    /// <summary>A copy could not be built, and the pool ended below its target.</summary>
    public const int TopUpIncomplete = 12;

    /// <summary>A user that belongs to no copy exists.</summary>
    public const int ForeignUsers = 13;

    /// <summary>At least one copy could not be deleted.</summary>
    public const int DeleteFailed = 14;

    /// <summary>The day's claims held the top-up below the pool's target.</summary>
    public const int ClaimCeiling = 15;

    /// <summary>The code <c>recycle</c> exits with.</summary>
    /// <remarks>
    /// "Low" and "empty" are about the pool the run FOUND, since that is what visitors met: its
    /// free copies however old (<see cref="PoolRunSummary.FreeAtStart"/>), since one too old to
    /// count towards the target is still free until the run deletes it. And only when it found a
    /// copy that is not a record: the first fill of an empty database finds no free copy and has
    /// turned nobody away. A pool whose every copy was deleted holds only
    /// records and is read the same way, so its next run exits 0 although visitors may have been
    /// turned away since the run before (ADR-0062).
    /// </remarks>
    public static int From(PoolRunSummary summary, int lowMark)
    {
        if (summary.ForeignUsers > 0)
        {
            return ForeignUsers;
        }

        if (summary.DeleteFailed > 0)
        {
            return DeleteFailed;
        }

        if (TheTopUpCameUpShort(summary))
        {
            return TopUpIncomplete;
        }

        if (summary.Ceiling)
        {
            return ClaimCeiling;
        }

        if (summary.RowsAtStart == 0)
        {
            return PoolOk;
        }

        if (summary.FreeAtStart == 0)
        {
            return PoolEmpty;
        }

        return summary.FreeAtStart < lowMark ? PoolLow : PoolOk;
    }

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

        return TheTopUpCameUpShort(summary) ? TopUpIncomplete : PoolOk;
    }

    /// <summary>The name a code is printed with: the last word of the summary line.</summary>
    /// <remarks>A code this class does not decide is printed as its number.</remarks>
    public static string Name(int exitCode) => exitCode switch
    {
        PoolOk => nameof(PoolOk),
        PoolLow => nameof(PoolLow),
        PoolEmpty => nameof(PoolEmpty),
        TopUpIncomplete => nameof(TopUpIncomplete),
        ForeignUsers => nameof(ForeignUsers),
        DeleteFailed => nameof(DeleteFailed),
        ClaimCeiling => nameof(ClaimCeiling),
        _ => exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    // Both halves: a copy that failed AND a pool that ended short. A copy that failed while the
    // target was still reached cost nothing; a pool that is short with no failure lost a copy to
    // a visitor while the run counted.
    private static bool TheTopUpCameUpShort(PoolRunSummary summary) =>
        summary.BuildFailed > 0 && summary.Free < summary.Target;
}
