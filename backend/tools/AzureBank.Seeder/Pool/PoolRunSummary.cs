namespace AzureBank.Seeder.Pool;

/// <summary>A copy that could not be built or deleted, and what the database said.</summary>
/// <param name="CopyId">The copy, so an operator can look at it.</param>
/// <param name="ErrorNumber">SQL Server's error number, when the failure was the database's.</param>
/// <param name="Message">The message of the exception at the root of the failure.</param>
public sealed record PoolCopyFailure(Guid CopyId, int? ErrorNumber, string Message);

/// <summary>What one run of <c>seed-pool</c> or <c>recycle</c> found and did.</summary>
/// <remarks>
/// Two instants. What the run FOUND is read before it changes anything: the pool rows, the free
/// copies, the day's claims, the clients at their cap, the users outside every copy. What it LEFT
/// is read at its end: the free copies, the claimed ones, the records of deleted copies.
/// </remarks>
public sealed record PoolRunSummary
{
    /// <summary>Pool rows that are not records of a deleted copy, before the run changed anything.</summary>
    public int RowsAtStart { get; init; }

    /// <summary>
    /// Free copies before the run changed anything, however old: what a visitor could have been
    /// given. The line prints it as <c>was</c>; "low" and "empty" are read from it.
    /// </summary>
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

    /// <summary>Claimed copies deleted because their time was over and no session was running in them.</summary>
    public int DeletedExpired { get; init; }

    /// <summary>
    /// Claimed copies deleted past the backstop while a grant of theirs was still live. Above 0 it
    /// says sign-in went on being accepted past a copy's end.
    /// </summary>
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
    /// <remarks>
    /// Every count, whatever the code: the code names one signal, and a run can carry several.
    /// Counts and a name only, never an id or an address, and always in this order, so the line
    /// can be searched and compared from one run to the next.
    /// </remarks>
    public string ToLine() =>
        $"pool: free={Free} was={FreeAtStart} claimed={Claimed} claims24h={Claims24h} clientsAtCap={ClientsAtCap} seeded={Seeded} "
        + $"deleted(expired={DeletedExpired} hardStop={DeletedHardStop} staleFree={DeletedStaleFree} failed={DeleteFailed}) "
        + $"swept(idempotency={SweptIdempotency} grants={SweptGrants}) "
        + $"tombstones={Tombstones} foreignUsers={ForeignUsers} ceiling={(Ceiling ? "yes" : "no")} "
        + $"result={PoolExitCodes.Name(ExitCode)}";
}
