namespace AzureBank.Tests.Fixtures;

/// <summary>
/// The <see cref="SqlServerFactAttribute"/> gate, for a <see cref="TheoryAttribute"/>.
/// </summary>
/// <remarks>
/// <para>
/// Same environment variable, same skip message, same reason: a proof that depends on real database
/// semantics must announce a missing prerequisite as SKIPPED, never as failed. Writing
/// <c>[Theory]</c> here by mistake makes every case fail on connection instead, which reads as a
/// broken suite rather than a machine without SQL Server — and that misreading is expensive on CI
/// and worse on somebody's first checkout.
/// </para>
/// <para>
/// It reads <see cref="SqlServerFactAttribute.ConnectionString"/> rather than the variable itself,
/// so the two gates cannot drift apart: change the variable in one place and both follow.
/// </para>
/// </remarks>
public sealed class SqlServerTheoryAttribute : TheoryAttribute
{
    public SqlServerTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(SqlServerFactAttribute.ConnectionString))
        {
            Skip = "Requires SQL Server - set "
                + $"{SqlServerFactAttribute.ConnectionStringVariable} to a connection string to run.";
        }
    }
}
