using AzureBank.Shared.Entities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The pool's table as the database enforces it: its columns, the two rules a row must keep, the
/// indexes the claim and the daily count read, and the key from a user to its copy.
/// </summary>
/// <remarks>
/// <para>
/// Read back from SQL Server's own catalogue and proved by statements the database refuses, not
/// from the model: the model says what the code intends, the catalogue says what a deployment has.
/// A claim that sets one of its two columns, or a copy marked deleted that nobody claimed, is a
/// state every later statement would have to defend against; here the database refuses to hold it.
/// </para>
/// <para>
/// Statements go over a plain <see cref="SqlConnection"/>, so each refusal is the server's own
/// error number and names the constraint.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DemoCopySchemaSqlServerTests
{
    private const int ConstraintViolated = 547;
    private const int DuplicateKeyInUniqueIndex = 2601;

    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<List<object?[]>> RowsAsync(DemoPoolDatabase database, string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Inserts one pool row; every column not given takes its default.</summary>
    private static async Task<int> InsertCopyAsync(
        DemoPoolDatabase database,
        Guid? id = null,
        Guid? owner = null,
        DateTime? claimedAt = null,
        Guid? claimId = null,
        DateTime? deletedAt = null)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO [DemoCopies] ([Id], [OwnerUserId], [CreatedAt], [ClaimedAt], [ClaimId], [DeletedAt]) "
            + "VALUES (@id, @owner, @createdAt, @claimedAt, @claimId, @deletedAt)";
        command.Parameters.Add(new SqlParameter("@id", id ?? Guid.CreateVersion7()));
        command.Parameters.Add(new SqlParameter("@owner", owner ?? Guid.CreateVersion7()));
        command.Parameters.Add(new SqlParameter("@createdAt", Now));
        command.Parameters.Add(new SqlParameter("@claimedAt", (object?)claimedAt ?? DBNull.Value));
        command.Parameters.Add(new SqlParameter("@claimId", (object?)claimId ?? DBNull.Value));
        command.Parameters.Add(new SqlParameter("@deletedAt", (object?)deletedAt ?? DBNull.Value));
        return await command.ExecuteNonQueryAsync();
    }

    private static ApplicationUser AUser(Guid? copy)
    {
        var id = Guid.CreateVersion7();
        var unique = id.ToString("N")[20..];
        return new ApplicationUser
        {
            Id = id,
            UserName = id.ToString(),
            NormalizedUserName = id.ToString().ToUpperInvariant(),
            Email = $"schema-{unique}@azurebank.example",
            NormalizedEmail = $"SCHEMA-{unique.ToUpperInvariant()}@AZUREBANK.EXAMPLE",
            AzureTag = $"schema_{unique}",
            FirstName = "Schema",
            LastName = "Proof",
            SecurityStamp = Guid.NewGuid().ToString(),
            DemoCopyId = copy,
        };
    }

    [SqlServerFact]
    public async Task ThePoolTable_HasTheEightColumns_AndIsEmptyOutsideTheDemo()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var columns = await RowsAsync(
            database,
            "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, COLUMN_DEFAULT "
            + "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'DemoCopies' ORDER BY COLUMN_NAME");

        columns.Select(c => $"{c[0]} {c[1]}{(c[2] is null ? "" : $"({c[2]})")} {(Equals(c[3], "YES") ? "NULL" : "NOT NULL")}")
            .Should().Equal(
                "ClaimedAt datetime2 NULL",
                "ClaimId uniqueidentifier NULL",
                "ClientKey binary(32) NULL",
                "CreatedAt datetime2 NOT NULL",
                "DeletedAt datetime2 NULL",
                "Id uniqueidentifier NOT NULL",
                "OwnerUserId uniqueidentifier NOT NULL",
                "Writes int NOT NULL");

        // A row that names no write count starts at 0: the default is the database's.
        (await InsertCopyAsync(database)).Should().Be(1);
        (await RowsAsync(database, "SELECT [Writes] FROM [DemoCopies]")).Single()[0].Should().Be(0);

        // The user's side: one nullable column, null on every user outside the demo.
        var userColumn = await RowsAsync(
            database,
            "SELECT DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS "
            + "WHERE TABLE_NAME = 'AspNetUsers' AND COLUMN_NAME = 'DemoCopyId'");
        userColumn.Should().ContainSingle().Which.Should().Equal("uniqueidentifier", "YES");
    }

    [SqlServerFact]
    public async Task AClaimIsWhole_ItsInstantAndItsIdAreSetTogetherOrNotAtAll()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var instantOnly = async () => await InsertCopyAsync(database, claimedAt: Now);
        var idOnly = async () => await InsertCopyAsync(database, claimId: Guid.CreateVersion7());

        (await instantOnly.Should().ThrowAsync<SqlException>()).Which.Should().Match<SqlException>(
            e => e.Number == ConstraintViolated && e.Message.Contains("CK_DemoCopies_ClaimIsWhole"));
        (await idOnly.Should().ThrowAsync<SqlException>()).Which.Should().Match<SqlException>(
            e => e.Number == ConstraintViolated && e.Message.Contains("CK_DemoCopies_ClaimIsWhole"));

        // CONTROLS: a free copy and a claimed one are both rows the table takes.
        (await InsertCopyAsync(database)).Should().Be(1);
        (await InsertCopyAsync(database, claimedAt: Now, claimId: Guid.CreateVersion7())).Should().Be(1);
    }

    [SqlServerFact]
    public async Task OnlyACopySomebodyClaimed_CanBeMarkedDeleted()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        var deletedFreeCopy = async () => await InsertCopyAsync(database, deletedAt: Now);

        (await deletedFreeCopy.Should().ThrowAsync<SqlException>()).Which.Should().Match<SqlException>(
            e => e.Number == ConstraintViolated && e.Message.Contains("CK_DemoCopies_DeletedWasClaimed"));

        // CONTROL: the record of a claimed copy that was deleted is a row the table takes.
        (await InsertCopyAsync(database, claimedAt: Now, claimId: Guid.CreateVersion7(), deletedAt: Now)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task AnOwnerHasOneCopy_AndAClaimNamesOneCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var owner = Guid.CreateVersion7();
        var claim = Guid.CreateVersion7();
        (await InsertCopyAsync(database, owner: owner, claimedAt: Now, claimId: claim)).Should().Be(1);

        var sameOwner = async () => await InsertCopyAsync(database, owner: owner);
        var sameClaim = async () => await InsertCopyAsync(database, claimedAt: Now, claimId: claim);

        (await sameOwner.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(DuplicateKeyInUniqueIndex);
        (await sameClaim.Should().ThrowAsync<SqlException>()).Which.Number.Should().Be(DuplicateKeyInUniqueIndex);

        // CONTROL: free copies all have a null claim id, and any number of them may exist.
        (await InsertCopyAsync(database)).Should().Be(1);
        (await InsertCopyAsync(database)).Should().Be(1);
    }

    [SqlServerFact]
    public async Task TheIndexesTheClaimAndTheDailyCountRead_ExistAndAreFiltered()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();

        const string indexes =
            "SELECT i.name, i.is_unique, ISNULL(i.filter_definition, ''), "
            + "STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) "
            + "FROM sys.indexes i "
            + "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 "
            + "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id "
            + "WHERE i.object_id = OBJECT_ID(@table) AND i.is_primary_key = 0 "
            + "GROUP BY i.name, i.is_unique, i.filter_definition";

        var pool = (await RowsAsync(database, indexes, new SqlParameter("@table", "DemoCopies")))
            .Select(i => $"{i[0]} on ({i[3]}) unique={i[1]} where {i[2]}")
            .ToList();

        // The claim's candidate read: free copies by age, without scanning the records of deleted ones.
        pool.Should().Contain("IX_DemoCopies_Free on (CreatedAt) unique=False where ([ClaimedAt] IS NULL)");
        // The daily count: one client's claims in the last 24 hours.
        pool.Should().Contain("IX_DemoCopies_ClientKey on (ClientKey,ClaimedAt) unique=False where ([ClientKey] IS NOT NULL)");
        pool.Should().Contain(i => i.Contains(" on (OwnerUserId) unique=True where ", StringComparison.Ordinal));
        pool.Should().Contain(i => i.Contains(" on (ClaimId) unique=True where ([ClaimId] IS NOT NULL)", StringComparison.Ordinal));

        // Every delete is keyed on the user's copy; outside the demo the index holds no row.
        var users = (await RowsAsync(database, indexes, new SqlParameter("@table", "AspNetUsers")))
            .Select(i => $"on ({i[3]}) unique={i[1]} where {i[2]}")
            .ToList();
        users.Should().Contain("on (DemoCopyId) unique=False where ([DemoCopyId] IS NOT NULL)");
    }

    [SqlServerFact]
    public async Task AUserPointsAtItsCopy_AndTheCopyCannotBeDeletedFromUnderIt()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copy = Guid.CreateVersion7();

        // No foreign key from the pool row to its owner: the record of a deleted copy outlives the
        // user, so a row may name an owner that is not there (yet, or any more).
        (await InsertCopyAsync(database, id: copy)).Should().Be(1);

        var key = await RowsAsync(
            database,
            "SELECT delete_referential_action_desc FROM sys.foreign_keys WHERE name = 'FK_AspNetUsers_DemoCopies_DemoCopyId'");
        key.Should().ContainSingle().Which.Single().Should().Be("NO_ACTION");

        await using (var db = database.NewContext())
        {
            // CONTROLS: a user of the copy, and a user of no copy.
            db.Users.AddRange(AUser(copy), AUser(copy: null));
            await db.SaveChangesAsync();
        }

        await using (var db = database.NewContext())
        {
            db.Users.Add(AUser(copy: Guid.CreateVersion7()));
            var userOfNoSuchCopy = async () => await db.SaveChangesAsync();

            (await userOfNoSuchCopy.Should().ThrowAsync<DbUpdateException>())
                .Which.InnerException.Should().BeOfType<SqlException>()
                .Which.Message.Should().Contain("FK_AspNetUsers_DemoCopies_DemoCopyId");
        }

        var deleteTheCopy = async () => await RowsAsync(
            database, "DELETE FROM [DemoCopies] WHERE [Id] = @id", new SqlParameter("@id", copy));

        (await deleteTheCopy.Should().ThrowAsync<SqlException>()).Which.Should().Match<SqlException>(
            e => e.Number == ConstraintViolated && e.Message.Contains("FK_AspNetUsers_DemoCopies_DemoCopyId"));

        await using (var db = database.NewContext())
        {
            (await db.Users.CountAsync(u => u.DemoCopyId == copy)).Should().Be(1);
            (await db.Users.CountAsync(u => u.DemoCopyId == null)).Should().Be(1);
        }
    }
}
