using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Records, for each save that fails with a <c>DbUpdateConcurrencyException</c>, the entity types
/// the exception names in <c>Entries</c>: what the code that classifies a conflict is given.
/// </summary>
/// <remarks>
/// A save can lose more than one row and name fewer: the batch stops at the first command that
/// matched no row, and the exception carries that command's entries. A proof that makes a save lose
/// two rows prints what this recorded, so that what it asserts next is read against the conflict
/// SQL Server and EF built and not against one built by hand.
/// </remarks>
public sealed class RefusedEntriesInterceptor : SaveChangesInterceptor
{
    private readonly ConcurrentQueue<string> _conflicts = new();

    /// <summary>One entry per conflicting save, in order: the entity type names, joined by a plus.</summary>
    public IReadOnlyCollection<string> Conflicts => _conflicts;

    public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
        ConcurrencyExceptionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        _conflicts.Enqueue(string.Join("+", eventData.Entries.Select(e => e.Metadata.ClrType.Name)));
        return ValueTask.FromResult(result);
    }
}
