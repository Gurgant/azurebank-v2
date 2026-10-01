using System.Net;
using System.Text.Json;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Tests.Integration;

/// <summary>
/// A handle is resolved only inside the caller's demo copy: one visitor can neither see nor pay
/// another visitor's users, and is told nothing about them.
/// </summary>
/// <remarks>
/// <para>
/// THE ANSWER IS THE TEST. A handle in another copy must be answered exactly as a handle nobody
/// holds, on every road that resolves one: the lookup (200, <c>exists: false</c>; it never answers
/// 404), the mint and the transfer (404 <c>Recipient</c>). Each foreign answer is compared with the
/// answer to an unknown handle of the same shape, as status, content type and body, with only the
/// trace id and the echoed handle taken out. A difference of one byte would tell a visitor that
/// some other copy uses that handle.
/// </para>
/// <para>
/// ON SQL SERVER, because the comparison of two copies is a comparison of two nullable columns.
/// In SQL, <c>NULL = NULL</c> is not true; outside the demo every user's copy is null, and two such
/// users must go on reaching each other. The InMemory provider compares nulls the C# way and would
/// pass either translation, so only the real provider can show that both branches hold: equal
/// copies, different copies, a copy against none, and none against none.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DemoIsolationSqlServerTests
{
    /// <summary>Six digits that are not the demo PIN.</summary>
    private const string WrongPin = "135790";

    /// <summary>A handle of a copy's shape that no copy in <paramref name="copies"/> holds.</summary>
    private static string UnknownHandleLike(string prefix, IReadOnlyList<BuiltCopy> copies)
    {
        var taken = copies.SelectMany(c => c.Users).Select(u => u.AzureTag).ToHashSet();
        return new[] { $"{prefix}_0000", $"{prefix}_1111", $"{prefix}_2222" }.First(handle => !taken.Contains(handle));
    }

    [SqlServerFact]
    public async Task AHandleInAnotherCopy_IsAnsweredByteForByteAsAnUnknownHandle_OnTheLookupTheMintAndTheTransfer()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var (mine, theirs) = (copies[0], copies[1]);
        await database.ClaimForAVisitorAsync(mine, DateTime.UtcNow);

        using var client = database.Api().CreateClient();
        var visitor = await DemoVisitor.SignInAsync(client, mine.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        var savings = (await visitor.AccountsAsync()).Single(a => a.IsPrimary);

        foreach (var (foreign, prefix) in new[] { (theirs.Owner.AzureTag, "john"), (theirs.Jane.AzureTag, "jane"), (theirs.Mike.AzureTag, "mike") })
        {
            var unknown = UnknownHandleLike(prefix, copies);

            // The lookup: 200 and "does not exist", never a 404 that would itself be a signal.
            using var lookedUp = await visitor.LookupAsync(foreign);
            using var lookedUpUnknown = await visitor.LookupAsync(unknown);
            lookedUp.StatusCode.Should().Be(HttpStatusCode.OK);
            var found = JsonSerializer.Deserialize<JsonElement>(await lookedUp.Content.ReadAsStringAsync()).GetProperty("data");
            found.GetProperty("exists").GetBoolean().Should().BeFalse("{0} belongs to another copy", foreign);
            found.GetProperty("displayName").GetString().Should().BeEmpty("no name crosses from one copy to another");
            (await DemoVisitor.AnswerAsync(lookedUp, foreign)).Should().Be(await DemoVisitor.AnswerAsync(lookedUpUnknown, unknown));

            // The mint: 404 Recipient, before the PIN is looked at. The PIN sent is WRONG, so a mint
            // that looked at it first would answer for the PIN and count an attempt; six of them
            // here would lock it.
            using var minted = await visitor.MintTransferAsync(savings.Id, foreign, 10m, WrongPin);
            using var mintedUnknown = await visitor.MintTransferAsync(savings.Id, unknown, 10m, WrongPin);
            minted.StatusCode.Should().Be(HttpStatusCode.NotFound, "{0} belongs to another copy", foreign);
            (await DemoVisitor.AnswerAsync(minted, foreign)).Should().Be(await DemoVisitor.AnswerAsync(mintedUnknown, unknown));

            // The transfer itself, presenting an authorisation that is worth nothing: the payee is
            // resolved before the authorisation is, so the same 404 comes first.
            using var sent = await visitor.TransferAsync(savings.Id, foreign, 10m, Guid.CreateVersion7());
            using var sentUnknown = await visitor.TransferAsync(savings.Id, unknown, 10m, Guid.CreateVersion7());
            sent.StatusCode.Should().Be(HttpStatusCode.NotFound, "{0} belongs to another copy", foreign);
            (await DemoVisitor.AnswerAsync(sent, foreign)).Should().Be(await DemoVisitor.AnswerAsync(sentUnknown, unknown));
        }

        await using var db = database.NewContext();
        (await db.StepUpAuthorizations.CountAsync()).Should().Be(0, "nothing was minted");
        (await db.Transactions.CountAsync()).Should().Be(52, "nothing moved: two copies of 26 rows");
        (await db.Users.Where(u => u.Id == mine.Owner.Id).Select(u => u.PinAccessFailedCount).SingleAsync())
            .Should().Be(0, "a refusal about the payee costs no PIN attempt, and six wrong PINs were sent");

        // CONTROL: the same wrong PIN to the copy's own contact IS looked at, refused and counted,
        // so the 404s above came before the PIN and not from a PIN nobody checks.
        using var refused = await visitor.MintTransferAsync(savings.Id, mine.Jane.AzureTag, 10m, WrongPin);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await db.Users.Where(u => u.Id == mine.Owner.Id).Select(u => u.PinAccessFailedCount).SingleAsync())
            .Should().Be(1, "a wrong PIN for a payee the caller can reach costs an attempt");
    }

    [SqlServerFact]
    public async Task AVisitor_FindsAndPaysTheContactsOfTheirOwnCopy()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(2);
        var mine = copies[0];
        await database.ClaimForAVisitorAsync(mine, DateTime.UtcNow);

        using var client = database.Api().CreateClient();
        var visitor = await DemoVisitor.SignInAsync(client, mine.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        var savings = (await visitor.AccountsAsync()).Single(a => a.IsPrimary);

        using var lookedUp = await visitor.LookupAsync(mine.Jane.AzureTag);
        var found = JsonSerializer.Deserialize<JsonElement>(await lookedUp.Content.ReadAsStringAsync()).GetProperty("data");
        found.GetProperty("exists").GetBoolean().Should().BeTrue("Jane is this copy's own contact");
        found.GetProperty("displayName").GetString().Should().Be("Jane S.");

        // The PIN every copy is seeded with mints, and the transfer lands on the contact's account.
        var authorization = await DemoVisitor.AuthorizationOfAsync(visitor.MintTransferAsync(savings.Id, mine.Jane.AzureTag, 40m));
        using var sent = await visitor.TransferAsync(savings.Id, mine.Jane.AzureTag, 40m, authorization);
        sent.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var db = database.NewContext();
        (await db.Accounts.Where(a => a.UserId == mine.Jane.Id).Select(a => a.Balance).SingleAsync()).Should().Be(8540.00m);
        (await db.Accounts.Where(a => a.Id == savings.Id).Select(a => a.Balance).SingleAsync()).Should().Be(12410.00m);
    }

    /// <summary>
    /// A GUARD: green on the code as it is, and it must stay green when the resolvers compare the
    /// copy. Outside the demo every user's copy is null, and on SQL Server a comparison of two nulls
    /// written as <c>=</c> matches no row: this is the test that would catch it.
    /// </summary>
    [SqlServerFact]
    public async Task TwoUsersOutsideEveryCopy_StillFindAndPayEachOther()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        using var client = database.Api().CreateClient();
        var (payer, _, payerAccount) = await DemoVisitor.RegisterAsync(client, "payer");
        var (_, payeeHandle, payeeAccount) = await DemoVisitor.RegisterAsync(client, "payee");
        (await payer.DepositAsync(payerAccount, 500m)).StatusCode.Should().Be(HttpStatusCode.Created);

        using var lookedUp = await payer.LookupAsync(payeeHandle);
        var found = JsonSerializer.Deserialize<JsonElement>(await lookedUp.Content.ReadAsStringAsync()).GetProperty("data");
        found.GetProperty("exists").GetBoolean().Should().BeTrue();
        found.GetProperty("displayName").GetString().Should().Be("Outside A.");

        var authorization = await DemoVisitor.AuthorizationOfAsync(payer.MintTransferAsync(payerAccount, payeeHandle, 120m));
        using var sent = await payer.TransferAsync(payerAccount, payeeHandle, 120m, authorization);
        sent.StatusCode.Should().Be(HttpStatusCode.Created);

        await using (var db = database.NewContext())
        {
            (await db.Accounts.Where(a => a.Id == payeeAccount).Select(a => a.Balance).SingleAsync()).Should().Be(120m);
        }
    }

    [SqlServerFact]
    public async Task ACopyAndAUserOutsideEveryCopy_CannotReachEachOther_InEitherDirection()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        var copies = await database.BuildCopiesAsync(1);
        var copy = copies[0];
        await database.ClaimForAVisitorAsync(copy, DateTime.UtcNow);

        using var client = database.Api().CreateClient();
        var visitor = await DemoVisitor.SignInAsync(client, copy.Owner.Email!, DemoPoolDatabase.VisitorPassword);
        var savings = (await visitor.AccountsAsync()).Single(a => a.IsPrimary);
        var (outsider, outsiderHandle, outsiderAccount) = await DemoVisitor.RegisterAsync(client, "outsider");

        // From the copy to the user outside it.
        var unknownOutside = "outsider_00000000";
        using var fromCopy = await visitor.LookupAsync(outsiderHandle);
        using var fromCopyUnknown = await visitor.LookupAsync(unknownOutside);
        (await DemoVisitor.AnswerAsync(fromCopy, outsiderHandle)).Should().Be(await DemoVisitor.AnswerAsync(fromCopyUnknown, unknownOutside));
        (await fromCopy.Content.ReadAsStringAsync()).Should().Contain("\"exists\":false");
        using var mintFromCopy = await visitor.MintTransferAsync(savings.Id, outsiderHandle, 10m);
        mintFromCopy.StatusCode.Should().Be(HttpStatusCode.NotFound, "a copy pays nobody outside itself");

        // From the user outside to the copy.
        var unknownInside = UnknownHandleLike("jane", copies);
        using var fromOutside = await outsider.LookupAsync(copy.Jane.AzureTag);
        using var fromOutsideUnknown = await outsider.LookupAsync(unknownInside);
        (await DemoVisitor.AnswerAsync(fromOutside, copy.Jane.AzureTag)).Should().Be(await DemoVisitor.AnswerAsync(fromOutsideUnknown, unknownInside));
        (await fromOutside.Content.ReadAsStringAsync()).Should().Contain("\"exists\":false");
        using var mintFromOutside = await outsider.MintTransferAsync(outsiderAccount, copy.Jane.AzureTag, 10m);
        mintFromOutside.StatusCode.Should().Be(HttpStatusCode.NotFound, "nobody outside a copy pays into it");
    }
}
