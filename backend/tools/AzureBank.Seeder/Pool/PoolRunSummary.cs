namespace AzureBank.Seeder.Pool;

/// <summary>A copy that could not be built or deleted, and what the database said.</summary>
/// <param name="CopyId">The copy, so an operator can look at it.</param>
/// <param name="ErrorNumber">SQL Server's error number, when the failure was the database's.</param>
/// <param name="Message">The message of the exception at the root of the failure.</param>
public sealed record PoolCopyFailure(Guid CopyId, int? ErrorNumber, string Message);

/// <summary>What one run of <c>seed-pool</c> or <c>recycle</c> found and did.</summary>
/// <remarks>NOT WRITTEN YET: the counts exist so the tests compile; the line is empty.</remarks>
public sealed record PoolRunSummary
{
    /// <summary>Pool rows that are not records of a deleted copy, before the run changed anything.</summary>
    public int RowsAtStart { get; init; }

    /// <summary>Fresh free copies before the run changed anything.</summary>
    public int FreeAtStart { get; init; }

    /// <summary>Fresh free copies at the end of the run.</summary>
    public int Free { get; init; }

    /// <summary>Fresh free copies the top-up aimed at.</summary>
    public int Target { get; init; }

    /// <summary>Claimed copies whose users still exist, at the end of the run.</summary>
    public int Claimed { get; init; }

    /// <summary>Claims in the last 24 hours.</summary>
    public int Claims24h { get; init; }

    /// <summary>Clients that reached their daily cap in the last 24 hours.</summary>
    public int ClientsAtCap { get; init; }

    /// <summary>Copies built by this run.</summary>
    public int Seeded { get; init; }

    /// <summary>Copies this run tried to build and could not.</summary>
    public int BuildFailed { get; init; }

    /// <summary>Claimed copies deleted because their time was over.</summary>
    public int DeletedExpired { get; init; }

    /// <summary>Claimed copies deleted past the backstop, whatever their grants.</summary>
    public int DeletedHardStop { get; init; }

    /// <summary>Free copies deleted because they were too old to hand out.</summary>
    public int DeletedStaleFree { get; init; }

    /// <summary>Copies whose delete threw.</summary>
    public int DeleteFailed { get; init; }

    /// <summary>Expired idempotency records removed.</summary>
    public int SweptIdempotency { get; init; }

    /// <summary>Expired grants removed.</summary>
    public int SweptGrants { get; init; }

    /// <summary>Records of deleted copies, at the end of the run.</summary>
    public int Tombstones { get; init; }

    /// <summary>Users that belong to no copy.</summary>
    public int ForeignUsers { get; init; }

    /// <summary>Whether the day's claims limited the top-up.</summary>
    public bool Ceiling { get; init; }

    /// <summary>The code the process exits with.</summary>
    public int ExitCode { get; init; }

    /// <summary>Each copy that could not be built or deleted.</summary>
    public IReadOnlyList<PoolCopyFailure> Failures { get; init; } = [];

    /// <summary>The run as one line for the job's log.</summary>
    public string ToLine() => string.Empty;
}
