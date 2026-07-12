using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.ValueGeneration;

namespace AzureBank.Infrastructure.Data.ValueGenerators;

/// <summary>
/// Generates UUID v7 (time-sortable) GUIDs for primary keys.
///
/// Benefits over random GUIDs (Guid.NewGuid() or NEWID()):
/// - Time-sortable: Better clustered index performance (no page splits)
/// - Client-side: No database round-trip needed
/// - Globally unique: Unlike NEWSEQUENTIALID() which resets on restart
/// - Portable: Works with any database (SQL Server, PostgreSQL, etc.)
///
/// Requires: .NET 9+
/// </summary>
public class GuidVersion7ValueGenerator : ValueGenerator<Guid>
{
    /// <summary>
    /// False = permanent value (not a temporary placeholder)
    /// </summary>
    public override bool GeneratesTemporaryValues => false;

    /// <summary>
    /// Generates a new UUID v7 (RFC 9562)
    /// Structure: 48-bit timestamp + 74-bit random
    /// </summary>
    public override Guid Next(EntityEntry entry) => Guid.CreateVersion7();
}
