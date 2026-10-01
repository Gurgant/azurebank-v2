namespace AzureBank.Seeder.Pool;

/// <summary>How the rows of one table leave when a copy is deleted.</summary>
public enum CopyRowFate
{
    /// <summary>The recycler deletes them with a statement of its own.</summary>
    Deleted,

    /// <summary>The database deletes them with the user they belong to.</summary>
    CascadesFromUser,
}

/// <summary>
/// What a control run leaves out of the delete. The tests' controls run the recycler's own
/// statements with one safeguard removed, so a control cannot drift from the code it is about.
/// </summary>
internal enum RecyclerControl
{
    /// <summary>The recycler as it ships.</summary>
    None,

    /// <summary>Delete the owner's rows only, not the two contacts'.</summary>
    OwnerOnly,

    /// <summary>Leave the soft-delete filter on, so closed accounts are not deleted.</summary>
    KeepSoftDeleteFilter,

    /// <summary>Let one copy's failure end the run.</summary>
    NoTryPerCopy,
}

/// <summary>Tops the pool up and deletes the copies whose time is over: what <c>recycle</c> runs.</summary>
/// <remarks>NOT WRITTEN YET: it does nothing, so its tests compile and fail.</remarks>
public sealed class DemoCopyRecycler
{
    /// <summary>Every table that holds rows of a copy's users, and how those rows leave.</summary>
    public static IReadOnlyDictionary<string, CopyRowFate> TablesOfACopy { get; } =
        new Dictionary<string, CopyRowFate>();

    /// <summary>Set by a test's control run only.</summary>
    internal RecyclerControl Control { get; set; }

    /// <summary>One run: top up, sweep, delete, count.</summary>
    public Task<PoolRunSummary> RunAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new PoolRunSummary());
}
