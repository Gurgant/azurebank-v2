extern alias seeder;

using System.Reflection;
using System.Text.RegularExpressions;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using seeder::AzureBank.Seeder.Pool;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// The edges of a demo copy, held where a later change would cross them unseen: the comparisons of
/// a handle, in the forms <see cref="HandleComparison"/> reads, every table that holds a copy's
/// rows, and the random source its identifiers and its password come from.
/// </summary>
/// <remarks>
/// <para>
/// A copy is three users that reach each other and nobody else, and it is deleted whole. Both
/// properties are true only of the code that exists today. A new query by handle that forgets the
/// copy would let one visitor see or pay another's users; a new table keyed on a user would make
/// every copy that wrote to it undeletable, or leave its rows behind. Neither would fail a test
/// written for the code as it is now, so these read the source and the model instead.
/// </para>
/// <para>
/// Source scans in the <see cref="SourceHygieneTests"/> shape, each with a liveness floor: a scan
/// that reads nothing reports clean for ever.
/// </para>
/// </remarks>
public class DemoCopyBoundaryTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the scan needs the sources; a guard that cannot run must fail loudly");
        return dir!;
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

    // ── Every comparison of a handle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A comparison of a handle in the forms a query by handle is written in here: <c>AzureTag ==</c>,
    /// <c>== x.AzureTag</c>, and an <c>Equals</c> call with a handle on either side.
    /// </summary>
    /// <remarks>
    /// A TEXT SCAN, and it sees only these forms: a comparison written another way, such as
    /// <c>!=</c> or a <c>ToLower()</c> before the <c>==</c>, passes it unseen. The two reversed
    /// forms and <c>Equals</c> are in it because a resolver written with them passed the first
    /// version of this scan, which read <c>AzureTag ==</c> alone.
    /// </remarks>
    private static readonly Regex HandleComparison = new(
        @"AzureTag\s*==|==\s*[\w.]*AzureTag\b|AzureTag\s*\.Equals\(|Equals\([^)]*AzureTag\b", RegexOptions.Compiled);

    private sealed record Site(string File, int Line, string Text, string[] Lines);

    /// <summary>What a comparison of a handle is for, and so what it owes the copy.</summary>
    private enum Role
    {
        /// <summary>It hands another user to the caller, so it must also compare the copy.</summary>
        HandsAnotherUserToTheCaller,

        /// <summary>It answers whether a handle is free in the whole table: the unique index is global.</summary>
        AsksWhetherAHandleIsTaken,

        /// <summary>It compares the caller's own row with what the caller typed.</summary>
        ReadsTheCallersOwnRow,
    }

    /// <summary>
    /// Every comparison of a handle in <c>backend/src</c> that <see cref="HandleComparison"/> sees,
    /// by the file it is in and the text that begins it. A comparison this table does not know
    /// fails the test below until someone decides which of the three it is.
    /// </summary>
    private static readonly (string File, string Begins, Role Role)[] Classified =
    [
        ("AuthService.cs", "AnyAsync(u => u.AzureTag == normalizedAzureTag", Role.AsksWhetherAHandleIsTaken),
        ("TransferService.cs", "senderUser.AzureTag.Equals(recipientAzureTag", Role.ReadsTheCallersOwnRow),
        ("TransferService.cs", "u.AzureTag == recipientAzureTag.ToLower()", Role.HandsAnotherUserToTheCaller),
        ("UserService.cs", ".Where(u => u.AzureTag == normalizedTag", Role.HandsAnotherUserToTheCaller),
        ("UserService.cs", "u => u.AzureTag == normalized && u.Id != userId", Role.AsksWhetherAHandleIsTaken),
        ("UserService.cs", "if (user.AzureTag == normalized)", Role.ReadsTheCallersOwnRow),
    ];

    private static List<Site> HandleComparisons()
    {
        var root = RepoRoot();
        var folder = Path.Combine(root.FullName, "backend", "src");
        Directory.Exists(folder).Should().BeTrue(because: $"expected to scan {folder}");

        var sites = new List<Site>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildOutput(file))
            {
                continue;
            }

            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (HandleComparison.IsMatch(lines[i]))
                {
                    sites.Add(new Site(Path.GetFileName(file), i + 1, lines[i].Trim(), lines));
                }
            }
        }

        scanned.Should().BeGreaterThan(100, "a scan that reads nothing reports clean for ever");
        return sites;
    }

    private static (string File, string Begins, Role Role)? ClassificationOf(Site site)
    {
        var known = Classified.Where(c => c.File == site.File && site.Text.Contains(c.Begins, StringComparison.Ordinal)).ToList();
        return known.Count == 1 ? known[0] : null;
    }

    /// <summary>
    /// A GUARD: green on the code as it is, and it stays green when the two resolvers gain their
    /// comparison of the copy. It goes red when a comparison of a handle appears that the table
    /// does not list.
    /// </summary>
    [Fact]
    public void EveryComparisonOfAHandleInTheBackend_IsOneThatWasClassified()
    {
        var sites = HandleComparisons();

        var unclassified = sites
            .Where(site => ClassificationOf(site) is null)
            .Select(site => $"{site.File}:{site.Line}  {site.Text}")
            .ToList();
        unclassified.Should().BeEmpty(
            "a query by handle either hands another user to the caller, and then it must compare the "
            + "copy as well, or it does not, and the table in this test says which");

        foreach (var known in Classified)
        {
            sites.Count(site => site.File == known.File && site.Text.Contains(known.Begins, StringComparison.Ordinal))
                .Should().Be(1, "'{0}' in {1} is one of the comparisons this table classifies; if it moved or changed, the table must follow", known.Begins, known.File);
        }

        sites.Should().HaveCount(Classified.Length);
    }

    [Fact]
    public void EveryComparisonThatHandsAnotherUserToTheCaller_AlsoComparesTheCopy()
    {
        var resolvers = HandleComparisons()
            .Where(site => ClassificationOf(site)?.Role == Role.HandsAnotherUserToTheCaller)
            .ToList();
        resolvers.Should().HaveCount(2, "the lookup, and the resolver the transfer and its mint share");

        foreach (var site in resolvers)
        {
            Statement(site).Should().MatchRegex(
                @"DemoCopyId\s*==",
                "{0}:{1} resolves a handle to another user, and a handle is resolved only inside the caller's copy",
                site.File, site.Line);
        }
    }

    /// <summary>The statement a line belongs to: from the line after the previous one that ended a statement or a block, to the first that ends this one.</summary>
    private static string Statement(Site site)
    {
        static bool Ends(string line) =>
            line.TrimEnd().EndsWith(';') || line.TrimEnd().EndsWith('{') || line.TrimEnd().EndsWith('}');

        var first = site.Line - 1;
        while (first > 0 && !Ends(site.Lines[first - 1]))
        {
            first--;
        }

        var last = site.Line - 1;
        while (last < site.Lines.Length - 1 && !site.Lines[last].TrimEnd().EndsWith(';'))
        {
            last++;
        }

        // Without its comments: a sentence about the copy is not a comparison of it.
        return string.Join(
            "\n",
            site.Lines[first..(last + 1)].Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    // ── Every table that holds a copy's rows ─────────────────────────────────────────────────────

    private static IModel Model()
    {
        // The model only: nothing connects.
        using var context = new AzureBankDbContext(
            new DbContextOptionsBuilder<AzureBankDbContext>()
                .UseSqlServer("Server=.;Database=DemoCopyBoundaryTests")
                .Options);
        return context.Model;
    }

    private static readonly Type[] WhatACopyIsMadeOf = [typeof(ApplicationUser), typeof(Account), typeof(Transaction)];

    /// <summary>
    /// The entities whose rows belong to a copy's users: the users themselves, everything with a
    /// foreign key to a user, an account or a ledger row, and everything that names a user in a
    /// <c>UserId</c> column no foreign key covers.
    /// </summary>
    private static List<IEntityType> EntitiesThatHoldACopysRows(IModel model) =>
    [
        .. model.GetEntityTypes().Where(entity =>
            entity.ClrType == typeof(ApplicationUser)
            || entity.GetForeignKeys().Any(key => WhatACopyIsMadeOf.Contains(key.PrincipalEntityType.ClrType))
            || (entity.FindProperty("UserId") is { } userId && !userId.IsForeignKey())),
    ];

    [Fact]
    public void EveryTableThatHoldsACopysRows_IsOneTheRecyclerNames()
    {
        var holders = EntitiesThatHoldACopysRows(Model());

        // Liveness: users, their four Identity tables, accounts, ledger rows, grants, notices, and
        // the two tables with a UserId and no foreign key.
        holders.Count.Should().BeGreaterThanOrEqualTo(11, "the walk found the tables that exist today");
        holders.Select(e => e.ClrType).Should().Contain(
            new[] { typeof(StepUpAuthorization), typeof(IdempotencyRecord) },
            "the two tables no foreign key covers are the ones a cascade would leave behind");

        var unnamed = holders
            .Select(entity => entity.GetTableName()!)
            .Where(table => !DemoCopyRecycler.TablesOfACopy.ContainsKey(table))
            .ToList();
        unnamed.Should().BeEmpty(
            "a table that holds rows of a copy's users and that the recycler does not name either blocks "
            + "the copy's delete or keeps its rows for ever; name it in DemoCopyRecycler.TablesOfACopy "
            + "and delete it there, or declare that it leaves with the user");
    }

    [Fact]
    public void ATableTheRecyclerLeavesToTheDatabase_ReallyCascadesFromTheUser()
    {
        var model = Model();
        var byTable = model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .ToDictionary(entity => entity.GetTableName()!);

        DemoCopyRecycler.TablesOfACopy.Should().NotBeEmpty("the recycler names the tables it empties");

        foreach (var (table, fate) in DemoCopyRecycler.TablesOfACopy)
        {
            byTable.Should().ContainKey(table, "the recycler names a table the model has");
            if (fate != CopyRowFate.CascadesFromUser)
            {
                continue;
            }

            var keys = byTable[table].GetForeignKeys().ToList();
            keys.Should().Contain(
                key => key.PrincipalEntityType.ClrType == typeof(ApplicationUser) && key.DeleteBehavior == DeleteBehavior.Cascade,
                "{0} is declared to leave with its user, so a cascading foreign key to the user must exist", table);
            keys.Where(key => WhatACopyIsMadeOf.Contains(key.PrincipalEntityType.ClrType))
                .Should().OnlyContain(
                    key => key.DeleteBehavior == DeleteBehavior.Cascade,
                    "a restricting key from {0} to a copy's rows would stop the delete of the user", table);
        }

        DemoCopyRecycler.TablesOfACopy.Should().Contain(
            new KeyValuePair<string, CopyRowFate>("AspNetUsers", CopyRowFate.Deleted), "the users are what the recycler deletes");
    }

    /// <summary>
    /// The foreign keys a copy's delete would run into: every key into a table the recycler empties,
    /// or into the pool's own table (a stale free copy's row is deleted), that comes from a table the
    /// recycler does not name and that the database does not empty with it. A table that cascades
    /// from one the delete reaches is reached in turn, since its rows leave in the same statement,
    /// and a key into IT would stop that statement: so the walk goes on until it finds no new table.
    /// </summary>
    /// <param name="model">The model to walk.</param>
    /// <param name="keysRead">How many foreign keys the walk read: a walk of nothing finds nothing.</param>
    private static List<string> KeysTheDeleteWouldMeet(IModel model, out int keysRead)
    {
        var reached = new HashSet<string>(DemoCopyRecycler.TablesOfACopy.Keys) { "DemoCopies" };
        var keys = model.GetEntityTypes()
            .Where(entity => entity.GetTableName() is not null)
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(key => key.PrincipalEntityType.GetTableName() is not null)
            .ToList();
        keysRead = keys.Count;

        bool grew;
        do
        {
            grew = false;
            foreach (var key in keys.Where(key => key.DeleteBehavior == DeleteBehavior.Cascade))
            {
                if (reached.Contains(key.PrincipalEntityType.GetTableName()!))
                {
                    grew |= reached.Add(key.DeclaringEntityType.GetTableName()!);
                }
            }
        }
        while (grew);

        // A key that sets the dependent's column to null does not stop the delete, and leaves a row
        // that is no longer the copy's.
        return
        [
            .. keys
                .Where(key => reached.Contains(key.PrincipalEntityType.GetTableName()!)
                    && !reached.Contains(key.DeclaringEntityType.GetTableName()!)
                    && key.DeleteBehavior != DeleteBehavior.SetNull)
                .Select(key => $"{key.DeclaringEntityType.GetTableName()} -> {key.PrincipalEntityType.GetTableName()} ({key.DeleteBehavior})")
                .Order(),
        ];
    }

    /// <summary>
    /// A GUARD, one level past <see cref="EveryTableThatHoldsACopysRows_IsOneTheRecyclerNames"/>, which
    /// finds the tables keyed on a user, an account or a ledger row. A table keyed on one of the
    /// recycler's other tables (an authorisation, a grant, a notice) or on the pool's row escapes
    /// that walk, and a key from it that restricts would make every such copy's delete fail with
    /// 547, run after run.
    /// </summary>
    [Fact]
    public void EveryKeyIntoATableTheDeleteEmpties_ComesFromATableTheRecyclerNames_OrLeavesWithIt()
    {
        var unmet = KeysTheDeleteWouldMeet(Model(), out var keysRead);

        keysRead.Should().BeGreaterThanOrEqualTo(13, "the walk read the foreign keys the model has today");
        unmet.Should().BeEmpty(
            "a table with a key into one the delete empties either stops the delete or keeps rows that name a deleted "
            + "row; name it in DemoCopyRecycler.TablesOfACopy and delete it there, or make its key cascade");
    }

    /// <summary>
    /// CONTROL for the guard above: a model with two tables the recycler does not know. One restricts
    /// the delete of an authorisation directly; the other restricts a table that itself leaves with
    /// an authorisation, two keys away from any user. The first walk saw neither.
    /// </summary>
    [Fact]
    public void TheWalk_FindsAKeyIntoARecyclersTable_AndOneTwoCascadesAway()
    {
        using var context = new ModelWithTablesTheRecyclerDoesNotKnow(
            new DbContextOptionsBuilder<AzureBankDbContext>().UseSqlServer("Server=.;Database=DemoCopyBoundaryTests").Options);

        var unmet = KeysTheDeleteWouldMeet(context.Model, out _);

        unmet.Should().Equal(
            "NotesOnAReceipt -> ReceiptsOfAStepUp (Restrict)",
            "UsesOfAStepUp -> StepUpAuthorizations (Restrict)");
        EntitiesThatHoldACopysRows(context.Model).Select(entity => entity.GetTableName()).Should().NotContain(
            new[] { "NotesOnAReceipt", "UsesOfAStepUp" },
            "the walk of tables keyed on a user, an account or a ledger row does not reach them: this guard is the one that does");
    }

    private sealed class UseOfAStepUp
    {
        public Guid Id { get; set; }

        public Guid StepUpId { get; set; }
    }

    private sealed class ReceiptOfAStepUp
    {
        public Guid Id { get; set; }

        public Guid StepUpId { get; set; }
    }

    private sealed class NoteOnAReceipt
    {
        public Guid Id { get; set; }

        public Guid ReceiptId { get; set; }
    }

    private sealed class ModelWithTablesTheRecyclerDoesNotKnow(DbContextOptions<AzureBankDbContext> options)
        : AzureBankDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<UseOfAStepUp>(use =>
            {
                use.ToTable("UsesOfAStepUp");
                use.HasOne<StepUpAuthorization>().WithMany().HasForeignKey(u => u.StepUpId).OnDelete(DeleteBehavior.Restrict);
            });
            modelBuilder.Entity<ReceiptOfAStepUp>(receipt =>
            {
                receipt.ToTable("ReceiptsOfAStepUp");
                receipt.HasOne<StepUpAuthorization>().WithMany().HasForeignKey(r => r.StepUpId).OnDelete(DeleteBehavior.Cascade);
            });
            modelBuilder.Entity<NoteOnAReceipt>(note =>
            {
                note.ToTable("NotesOnAReceipt");
                note.HasOne<ReceiptOfAStepUp>().WithMany().HasForeignKey(n => n.ReceiptId).OnDelete(DeleteBehavior.Restrict);
            });
        }
    }

    // ── The recycler's controls ──────────────────────────────────────────────────────────────────

    /// <summary>The lines of a source file that are code: no line that is only a comment.</summary>
    private static IEnumerable<string> CodeLines(string file) =>
        File.ReadAllLines(file).Where(line =>
        {
            var text = line.TrimStart();
            return !text.StartsWith("//", StringComparison.Ordinal)
                && !text.StartsWith('*')
                && !text.StartsWith("/*", StringComparison.Ordinal);
        });

    /// <summary>
    /// A GUARD: green while the recycler's controls are named by the recycler alone. A control runs
    /// the delete with one safeguard taken out (the contacts' rows, the closed accounts, the try
    /// around each copy); it exists for the tests that show what each safeguard is for. A command
    /// that set one would ship a delete that fails on purpose, so no code that ships may name the
    /// type: every value but the default has to be named to be set.
    /// </summary>
    [Fact]
    public void TheRecyclersControls_AreNamedByNoCodeThatShips_ButTheRecyclerItself()
    {
        var backend = Path.Combine(RepoRoot().FullName, "backend");
        var scanned = 0;
        var naming = new List<string>();
        foreach (var folder in new[] { "src", "tools" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(backend, folder), "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                {
                    continue;
                }

                scanned++;
                if (CodeLines(file).Any(line => line.Contains("RecyclerControl", StringComparison.Ordinal)))
                {
                    naming.Add(Path.GetFileName(file));
                }
            }
        }

        scanned.Should().BeGreaterThan(100, "a scan that reads nothing reports clean for ever");
        naming.Should().Equal(
            new[] { "DemoCopyRecycler.cs" },
            "the controls are declared and read in the recycler, and set by tests only");
    }

    // ── One history ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The fixed demo and a visitor's copy show the same history, so there is one place that holds
    /// it. A second table of rows in the fixed demo's seeder would be free to drift from the one a
    /// copy is built from, and no test of either would notice.
    /// </summary>
    [Fact]
    public void TheFixedDemo_TakesItsHistoryFromTheLedgerACopyIsBuiltFrom()
    {
        var file = Path.Combine(
            RepoRoot().FullName, "backend", "tools", "AzureBank.Seeder", "Seeders", "TransactionSeeder.cs");
        File.Exists(file).Should().BeTrue(because: $"expected to read {file}");
        var code = string.Join("\n", CodeLines(file));

        code.Should().Contain("DemoLedger.Build(", "the fixed demo's rows are the ones DemoLedger builds");
        code.Should().NotContain(
            "TransactionType.",
            "a deposit or a withdrawal written down in the seeder is a second copy of the history");
    }

    // ── The random source ────────────────────────────────────────────────────────────────────────

    private static readonly Regex SystemRandom = new(@"\bRandom\b", RegexOptions.Compiled);

    private static string[] PoolSources()
    {
        var folder = Path.Combine(RepoRoot().FullName, "backend", "tools", "AzureBank.Seeder", "Pool");
        Directory.Exists(folder).Should().BeTrue(because: $"expected to scan {folder}");
        var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)).ToArray();
        files.Select(f => Path.GetFileName(f)).Should().Contain(
            new[] { "DemoCredentials.cs", "DemoCopyBuilder.cs", "DemoCopyRecycler.cs" },
            "a scan that reads nothing reports clean for ever");
        return files;
    }

    /// <summary>
    /// A GUARD: green until somebody reaches for <c>System.Random</c> in the pool's code. A copy's
    /// email address is the only thing between a stranger and a claimed copy's sign-in form, so it
    /// must not come from a generator whose next value can be computed from its last.
    /// </summary>
    [Fact]
    public void ThePoolsCode_NeverUsesSystemRandom()
    {
        var offenders = new List<string>();
        foreach (var file in PoolSources())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i].TrimStart();
                var isComment = text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('*') || text.StartsWith("/*", StringComparison.Ordinal);
                if (!isComment && SystemRandom.IsMatch(text))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {text}");
                }
            }
        }

        offenders.Should().BeEmpty("every random character of a copy comes from RandomNumberGenerator");
    }

    [Fact]
    public void ACopysIdentifiers_AreDrawnFromTheCryptographicGenerator()
    {
        var source = File.ReadAllText(PoolSources().Single(f => Path.GetFileName(f) == "DemoCredentials.cs"));

        source.Should().Contain(
            "RandomNumberGenerator.GetInt32(",
            "each character is drawn with GetInt32, which is uniform over the alphabet; a byte and a modulo would not be");
    }

    /// <summary>
    /// The files in which a claim makes what it hands to a visitor or stores about one. Named one
    /// by one, and each must be there: they sit in folders that hold other code, and a file moved
    /// away would otherwise leave the scan reading nothing and reporting clean.
    /// </summary>
    private static string[] ClaimSources()
    {
        var api = Path.Combine(RepoRoot().FullName, "backend", "src", "AzureBank.Api");
        string[] files =
        [
            Path.Combine(api, "Security", "DemoPasswordGenerator.cs"),
            Path.Combine(api, "Security", "DemoClientKey.cs"),
        ];
        foreach (var file in files)
        {
            File.Exists(file).Should().BeTrue(because: $"expected to scan {file}");
        }

        return files;
    }

    /// <summary>
    /// A GUARD: green until somebody reaches for <c>System.Random</c> in the claim's code. A claimed
    /// copy is signed in to with the password its claim answered, so that password must not come
    /// from a generator whose next value can be computed from its last.
    /// </summary>
    [Fact]
    public void TheClaimsCode_NeverUsesSystemRandom()
    {
        var offenders = new List<string>();
        foreach (var file in ClaimSources())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var text = lines[i].TrimStart();
                var isComment = text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('*') || text.StartsWith("/*", StringComparison.Ordinal);
                if (!isComment && SystemRandom.IsMatch(text))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {text}");
                }
            }
        }

        offenders.Should().BeEmpty("whatever a claim draws, it draws from RandomNumberGenerator");
    }

    [Fact]
    public void ACopysPassword_IsDrawnFromTheCryptographicGenerator()
    {
        // The code lines only: the file's remarks name the call too, and a remark draws nothing.
        var file = ClaimSources().Single(f => Path.GetFileName(f) == "DemoPasswordGenerator.cs");
        var code = string.Join("\n", CodeLines(file));

        code.Should().Contain(
            "RandomNumberGenerator.GetInt32(",
            "each character is drawn with GetInt32, which is uniform over the alphabet; a byte and a modulo would not be");
    }
}
