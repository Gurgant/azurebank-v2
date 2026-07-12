using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// Test-only model customizer for the EF InMemory provider.
///
/// SQL Server generates rowversion columns (Account.RowVersion) on the server;
/// the InMemory provider generates nothing for them, so inserts fail with
/// "Required properties '{'RowVersion'}' are missing". For in-memory tests the
/// optimistic-concurrency machinery is irrelevant (concurrency tests are the
/// explicit SQL Server skips), so byte[] concurrency tokens are downgraded to
/// plain nullable columns here.
///
/// Applied only on the fixture's in-memory path via
/// options.ReplaceService&lt;IModelCustomizer, InMemoryTestModelCustomizer&gt;();
/// the Testcontainers/SQL Server path keeps full fidelity.
/// </summary>
public sealed class InMemoryTestModelCustomizer : ModelCustomizer
{
    public InMemoryTestModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(byte[]) && property.IsConcurrencyToken)
                {
                    property.IsConcurrencyToken = false;
                    property.ValueGenerated = ValueGenerated.Never;
                    property.IsNullable = true;
                }
            }
        }
    }
}
