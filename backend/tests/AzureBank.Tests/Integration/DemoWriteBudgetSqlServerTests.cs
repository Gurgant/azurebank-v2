using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Common;
using AzureBank.Shared.Exceptions;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureBank.Tests.Integration;

/// <summary>
/// The budget of one demo copy on SQL Server: with the demo on, a copy's users can make
/// <c>Demo:Copy:MaxWrites</c> requests that change something, the reveal of an account number
/// among them, and the next one is refused with 429 <c>DEMO_COPY_LIMIT</c>.
/// </summary>
/// <remarks>
/// <para>
/// ON SQL SERVER, because the budget is one statement that checks the limit and adds one together,
/// and that is what makes it hold when several requests of one copy arrive at once. The InMemory
/// provider has no such statement: there the middleware reads, checks, adds and saves
/// (<c>DemoWriteBudgetMiddlewareTests</c>, <c>DemoWriteBudgetEndpointTests</c>).
/// </para>
/// <para>
/// Every test has a scratch database of its own (<see cref="DemoPoolDatabase"/>), copies built by
/// the Seeder's own builder, and an API with the demo on and the limit at 10, the lowest a host
/// starts with. A copy is claimed through the API, as a visitor claims one, and its owner acts on
/// the session that claim opened.
/// </para>
/// </remarks>
[Trait("Category", "SqlServer")]
[Collection(SqlServerProofsCollection.Name)]
public sealed class DemoWriteBudgetSqlServerTests
{
    private const int Limit = 10;

    /// <summary>Six digits that are not the demo PIN.</summary>
    private const string WrongPin = "135790";

    private static CustomWebApplicationFactory DemoApiOf(DemoPoolDatabase database) =>
        database.DemoApi(("Demo:Copy:MaxWrites", "10"));

    /// <summary>Claims a free copy through the API and returns it with its owner, on the session the claim opened.</summary>
    private static async Task<(BuiltCopy Copy, DemoVisitor Owner, DemoClaimResponse Claim)> ClaimACopyAsync(
        DemoPoolDatabase database, HttpClient client, string address = "203.0.113.7")
    {
        using var response = await DemoVisitor.ClaimAsync(client, address);
        var claim = await DemoVisitor.ClaimedAsync(response);
        var copy = (await database.CopiesAsync()).Single(c => c.Row.OwnerUserId == claim.User.Id);
        return (copy, DemoVisitor.OfClaim(client, claim), claim);
    }

    private static async Task<int> WritesOfAsync(DemoPoolDatabase database, Guid copyId)
    {
        await using var db = database.NewContext();
        return await db.DemoCopies.AsNoTracking().Where(c => c.Id == copyId).Select(c => c.Writes).SingleAsync();
    }

    /// <summary>ARRANGE: a copy that has already made <paramref name="writes"/> changes.</summary>
    private static async Task SetWritesAsync(DemoPoolDatabase database, Guid copyId, int writes)
    {
        await using var db = database.NewContext();
        (await db.DemoCopies.Where(c => c.Id == copyId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Writes, writes)))
            .Should().Be(1, "ARRANGE: the copy exists");
    }

    private static async Task<int> AuditRowsAsync(DemoPoolDatabase database)
    {
        await using var db = database.NewContext();
        return await db.AuditEvents.CountAsync();
    }

    /// <summary>
    /// A response as status, media type and body, without the trace id, and with parentheses where
    /// the body has braces: a brace in a text handed to an assertion's message breaks the message.
    /// </summary>
    private static async Task<string> AnswerOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 0 && JsonNode.Parse(body) is JsonObject json)
        {
            json.Remove("traceId");
            body = json.ToJsonString();
        }

        return $"{(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType} {body}"
            .Replace('{', '(').Replace('}', ')');
    }

    /// <summary>The refusal of a change past a copy's limit: its status, its code, its sentence, and no wait named anywhere.</summary>
    private static async Task ShouldBeTheCopyLimitAsync(HttpResponseMessage response)
    {
        var answer = await AnswerOfAsync(response);
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the copy is at its limit ({0})", answer);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using (new AssertionScope())
        {
            body.RootElement.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.DemoCopyLimit);
            body.RootElement.GetProperty("detail").GetString().Should().Be(DemoRefusalException.CopyLimitDetail);
            body.RootElement.TryGetProperty("retryAfterSeconds", out _).Should().BeFalse(
                "a copy's limit does not end at an instant: only a fresh copy helps");
            response.Headers.Contains("Retry-After").Should().BeFalse();
        }
    }

    // ── The limit ────────────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task TheEleventhChange_Is429CopyLimit_AndAReadStillAnswers()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        using var client = DemoApiOf(database).CreateClient();
        var (copy, owner, _) = await ClaimACopyAsync(database, client);
        var savings = (await owner.AccountsAsync()).Single(a => a.IsPrimary);

        (await WritesOfAsync(database, copy.Id)).Should().Be(
            0, "the claim is the BFF's own request, and listing the accounts changes nothing: neither is counted");

        for (var change = 1; change <= Limit; change++)
        {
            using var deposited = await owner.DepositAsync(savings.Id, 5m);

            deposited.StatusCode.Should().Be(
                HttpStatusCode.Created, "change {0} of {1} is inside the budget ({2})", change, Limit, await AnswerOfAsync(deposited));
            (await WritesOfAsync(database, copy.Id)).Should().Be(change, "each change is counted once");
        }

        var eleventhKey = Guid.NewGuid();
        using var eleventh = await owner.DepositAsync(savings.Id, 5m, eleventhKey);

        await ShouldBeTheCopyLimitAsync(eleventh);
        using var read = await owner.ListAccountsAsync();
        await using var db = database.NewContext();
        var records = await db.IdempotencyRecords.AsNoTracking().Where(r => r.UserId == copy.Owner.Id).ToListAsync();
        using (new AssertionScope())
        {
            (await WritesOfAsync(database, copy.Id)).Should().Be(Limit, "a refused change is not counted: the count stops at the limit");

            // A read still answers: a copy at its limit can be looked at, and what it shows is the
            // ten deposits and not the eleventh.
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            var accounts = (await read.Content.ReadFromJsonAsync<ApiResponse<List<AccountResponse>>>(DemoVisitor.Json))!.Data!;
            accounts.Single(a => a.Id == savings.Id).Balance.Should().Be(savings.Balance + (Limit * 5m));

            // Nothing was written for the refused request: the budget is asked before the
            // idempotency claim is made.
            records.Should().HaveCount(Limit, "one record for each deposit that was made");
            records.Should().NotContain(r => r.Key == eleventhKey);
        }
    }

    [SqlServerFact]
    public async Task AnotherCopy_IsNotAffected()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(2);
        using var client = DemoApiOf(database).CreateClient();
        var (full, fullOwner, _) = await ClaimACopyAsync(database, client);
        var (other, otherOwner, _) = await ClaimACopyAsync(database, client);
        other.Id.Should().NotBe(full.Id, "ARRANGE: two claims, two copies");
        await SetWritesAsync(database, full.Id, Limit);
        var fullSavings = (await fullOwner.AccountsAsync()).Single(a => a.IsPrimary);
        var otherSavings = (await otherOwner.AccountsAsync()).Single(a => a.IsPrimary);

        // CONTROL: the first copy is at its limit, and its owner is refused.
        using var refused = await fullOwner.DepositAsync(fullSavings.Id, 5m);
        await ShouldBeTheCopyLimitAsync(refused);

        using var deposited = await otherOwner.DepositAsync(otherSavings.Id, 5m);

        using (new AssertionScope())
        {
            deposited.StatusCode.Should().Be(
                HttpStatusCode.Created, "another copy's limit is not this copy's ({0})", await AnswerOfAsync(deposited));
            (await WritesOfAsync(database, other.Id)).Should().Be(1, "the change is counted on the caller's own copy");
            (await WritesOfAsync(database, full.Id)).Should().Be(Limit, "and on no other");
        }
    }

    // ── What is counted ──────────────────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task ARequestTheApiRefuses_IsCountedToo()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        using var client = DemoApiOf(database).CreateClient();
        var (copy, owner, _) = await ClaimACopyAsync(database, client);
        var savings = (await owner.AccountsAsync()).Single(a => a.IsPrimary);

        // The budget is spent before the request is looked at: what the API then says to it does
        // not give the change back. Each of these four is counted, and none changes anything.
        using var wrongPin = await owner.VerifyPinAsync(WrongPin);
        var afterTheWrongPin = await WritesOfAsync(database, copy.Id);
        using var noKey = await owner.RequestAsync(HttpMethod.Post, "/api/transactions/deposit");
        var afterNoKey = await WritesOfAsync(database, copy.Id);
        using var nothingToDeposit = await owner.DepositAsync(savings.Id, -5m);
        var afterNothingToDeposit = await WritesOfAsync(database, copy.Id);
        using var wrongPinOnATransfer = await owner.MintTransferAsync(savings.Id, copy.Jane.AzureTag, 10m, WrongPin);
        var afterTheMint = await WritesOfAsync(database, copy.Id);

        using (new AssertionScope())
        {
            // A wrong PIN is told so in a 200; the three others are refused by their status.
            wrongPin.StatusCode.Should().Be(HttpStatusCode.OK, await AnswerOfAsync(wrongPin));
            (JsonNode.Parse(await wrongPin.Content.ReadAsStringAsync())?["data"]?["verified"]?.GetValue<bool>())
                .Should().Be(false, "the PIN was looked at, and it is not the copy's");
            afterTheWrongPin.Should().Be(1, "a PIN verification that fails is counted");

            noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest, await AnswerOfAsync(noKey));
            (await DemoVisitor.ErrorCodeOfAsync(noKey)).Should().Be(ErrorCodes.IdempotencyKeyMissing);
            afterNoKey.Should().Be(2, "a deposit refused for its missing key is counted: the budget is asked before the key is");

            nothingToDeposit.StatusCode.Should().Be(HttpStatusCode.BadRequest, await AnswerOfAsync(nothingToDeposit));
            afterNothingToDeposit.Should().Be(3, "a deposit refused for its amount is counted");

            wrongPinOnATransfer.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await AnswerOfAsync(wrongPinOnATransfer));
            afterTheMint.Should().Be(4, "a transfer refused for its PIN is counted");

            await using var db = database.NewContext();
            (await db.Accounts.Where(a => a.Id == savings.Id).Select(a => a.Balance).SingleAsync())
                .Should().Be(savings.Balance, "none of the four moved anything");
        }
    }

    [SqlServerFact]
    public async Task WhatChangesNothing_OrComesFromNobody_IsNotCounted()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        using var client = DemoApiOf(database).CreateClient();
        var (copy, owner, _) = await ClaimACopyAsync(database, client);
        var savings = (await owner.AccountsAsync()).Single(a => a.IsPrimary);

        using var lookedUp = await owner.LookupAsync(copy.Jane.AzureTag);
        using var listed = await owner.ListAccountsAsync();
        using var noSuchPath = await owner.RequestAsync(HttpMethod.Post, "/api/no-such-path");
        using var nobody = await client.PostAsJsonAsync("/api/auth/pin/verify", new VerifyPinRequest { Pin = "123456" }, DemoVisitor.Json);

        using (new AssertionScope())
        {
            lookedUp.StatusCode.Should().Be(HttpStatusCode.OK);
            listed.StatusCode.Should().Be(HttpStatusCode.OK);
            noSuchPath.StatusCode.Should().Be(HttpStatusCode.NotFound, "no endpoint answers that path, so there is nothing to change");
            nobody.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "nobody is signed in: there is no caller whose copy could be counted");
            (await WritesOfAsync(database, copy.Id)).Should().Be(0, "two reads, a path with no endpoint and a request from nobody: none is a change by this copy");
        }

        // CONTROL: on the same host the same user's deposit is counted.
        using var deposited = await owner.DepositAsync(savings.Id, 5m);
        deposited.StatusCode.Should().Be(HttpStatusCode.Created);
        (await WritesOfAsync(database, copy.Id)).Should().Be(1, "CONTROL: a change is counted");
    }

    [SqlServerFact]
    public async Task TheReveal_IsCounted_AndRefusedAtTheLimit()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        using var client = DemoApiOf(database).CreateClient();
        var (copy, owner, _) = await ClaimACopyAsync(database, client);
        var savings = (await owner.AccountsAsync()).Single(a => a.IsPrimary);
        await SetWritesAsync(database, copy.Id, Limit - 1);
        var auditRowsBefore = await AuditRowsAsync(database);

        using var revealed = await owner.RevealAsync(savings.Id);

        var auditRowsAfterTheReveal = await AuditRowsAsync(database);
        using (new AssertionScope())
        {
            revealed.StatusCode.Should().Be(HttpStatusCode.OK, await AnswerOfAsync(revealed));
            (await WritesOfAsync(database, copy.Id)).Should().Be(Limit, "the reveal is a GET that writes, and it is counted as a change");
            auditRowsAfterTheReveal.Should().Be(auditRowsBefore + 1, "CONTROL: every reveal writes a row of the audit trail");
        }

        using var refused = await owner.RevealAsync(savings.Id);

        await ShouldBeTheCopyLimitAsync(refused);
        using (new AssertionScope())
        {
            (await AuditRowsAsync(database)).Should().Be(
                auditRowsAfterTheReveal, "the refused reveal wrote no audit row: that is what the limit is for");
            (await WritesOfAsync(database, copy.Id)).Should().Be(Limit);
            (await refused.Content.ReadAsStringAsync()).Should().NotContain(
                "accountNumber", "a refusal carries no account number");
        }
    }

    [SqlServerFact]
    public async Task AChangeSentAgainUnderItsKey_IsAnsweredFromWhatWasStored_AndCountedAgain()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        using var client = DemoApiOf(database).CreateClient();
        var (copy, owner, _) = await ClaimACopyAsync(database, client);
        var savings = (await owner.AccountsAsync()).Single(a => a.IsPrimary);
        await SetWritesAsync(database, copy.Id, Limit - 2);
        var key = Guid.NewGuid();

        using var first = await owner.DepositAsync(savings.Id, 5m, key);
        using var again = await owner.DepositAsync(savings.Id, 5m, key);
        using var pastTheLimit = await owner.DepositAsync(savings.Id, 5m, key);

        await using var db = database.NewContext();
        using (new AssertionScope())
        {
            first.StatusCode.Should().Be(HttpStatusCode.Created, await AnswerOfAsync(first));
            first.Headers.Contains(IdempotencyConstants.ReplayedHeaderName).Should().BeFalse("CONTROL: the first one is made, not replayed");

            // The same deposit under the same key is a retry: answered from what was stored, and
            // counted all the same, because the budget is asked before the key is looked up.
            again.StatusCode.Should().Be(HttpStatusCode.Created, await AnswerOfAsync(again));
            again.Headers.TryGetValues(IdempotencyConstants.ReplayedHeaderName, out var replayed).Should().BeTrue();
            replayed.Should().Equal("true");
            (await WritesOfAsync(database, copy.Id)).Should().Be(Limit, "the deposit and its retry are two requests, and each is counted");

            (await db.Accounts.Where(a => a.Id == savings.Id).Select(a => a.Balance).SingleAsync())
                .Should().Be(savings.Balance + 5m, "the money moved once");
        }

        // And at the limit a retry is refused like any other change, not answered from the store.
        await ShouldBeTheCopyLimitAsync(pastTheLimit);
    }

    // ── What is never refused, and where nothing is counted ──────────────────────────────────────

    [SqlServerFact]
    public async Task SignOutEverywhere_IsNeverRefused()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(2);
        using var client = DemoApiOf(database).CreateClient();
        var (fresh, freshOwner, _) = await ClaimACopyAsync(database, client);
        var (full, fullOwner, _) = await ClaimACopyAsync(database, client);
        await SetWritesAsync(database, full.Id, Limit);
        var fullSavings = (await fullOwner.AccountsAsync()).Single(a => a.IsPrimary);

        // What signing out everywhere answers a copy that has changed nothing.
        using var fromTheFreshCopy = await freshOwner.SignOutEverywhereAsync();
        fromTheFreshCopy.StatusCode.Should().Be(HttpStatusCode.OK, "ARRANGE: {0}", await AnswerOfAsync(fromTheFreshCopy));

        // CONTROL: the other copy is at its limit, and a change of its owner's is refused.
        using var refused = await fullOwner.DepositAsync(fullSavings.Id, 5m);
        await ShouldBeTheCopyLimitAsync(refused);

        using var fromTheFullCopy = await fullOwner.SignOutEverywhereAsync();

        await using var db = database.NewContext();
        using (new AssertionScope())
        {
            (await AnswerOfAsync(fromTheFullCopy)).Should().Be(
                await AnswerOfAsync(fromTheFreshCopy), "signing out everywhere is answered the same whatever the copy has spent");
            (await db.RefreshTokens.CountAsync(t => t.UserId == full.Owner.Id && t.RevokedAt == null))
                .Should().Be(0, "and it was done: no grant of the user is left standing");
            (await db.RefreshTokens.CountAsync(t => t.UserId == full.Owner.Id))
                .Should().Be(1, "CONTROL: the claim had opened one session to end");
            (await WritesOfAsync(database, full.Id)).Should().Be(Limit, "it is not a change of the copy: nothing is counted");
            (await WritesOfAsync(database, fresh.Id)).Should().Be(0, "on either copy");
        }
    }

    [SqlServerFact]
    public async Task WithTheDemoOff_TheSameUser_IsNeverCounted()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        using var demoClient = DemoApiOf(database).CreateClient();
        var (copy, onTheDemo, claim) = await ClaimACopyAsync(database, demoClient);

        // The same user, signed in to a host on the same database that does not turn the demo on.
        using var ordinaryClient = database.Api().CreateClient();
        var owner = await DemoVisitor.SignInAsync(ordinaryClient, claim.Copy.Email, claim.Copy.Password);
        owner.UserId.Should().Be(copy.Owner.Id, "ARRANGE: the copy's owner");
        var savings = (await owner.AccountsAsync()).Single(a => a.IsPrimary);

        for (var change = 1; change <= 3; change++)
        {
            using var deposited = await owner.DepositAsync(savings.Id, 5m);
            deposited.StatusCode.Should().Be(HttpStatusCode.Created, await AnswerOfAsync(deposited));
        }

        (await WritesOfAsync(database, copy.Id)).Should().Be(0, "where the demo is off there is no budget, and nothing is counted");

        // CONTROL: the same user's deposit through the demo's host is counted.
        using var counted = await onTheDemo.DepositAsync(savings.Id, 5m);
        counted.StatusCode.Should().Be(HttpStatusCode.Created);
        (await WritesOfAsync(database, copy.Id)).Should().Be(1, "CONTROL: this user is one the budget counts");
    }

    // ── The statement ────────────────────────────────────────────────────────────────────────────

    /// <summary>Every statement a host sends from <see cref="Start"/> on, in order, with whether a transaction was open for it.</summary>
    private sealed class StatementsSent : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<(string Text, bool InTransaction)> _sent = new();
        private int _recording;

        public void Start() => Volatile.Write(ref _recording, 1);

        public IReadOnlyList<(string Text, bool InTransaction)> All => [.. _sent];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Record(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Record(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            if (Volatile.Read(ref _recording) == 1)
            {
                _sent.Enqueue((command.CommandText.Trim(), command.Transaction is not null));
            }
        }
    }

    private static bool Names(string statement, string table) => statement.Contains($"[{table}]", StringComparison.Ordinal);

    /// <summary>Whether a statement is the one that claims an idempotency key: the first thing a deposit that goes on writes.</summary>
    private static bool WritesAKeysClaim(string statement) =>
        statement.Contains("INSERT INTO [IdempotencyRecords]", StringComparison.Ordinal);

    /// <summary>
    /// The budget's statement: an update of the pool's table that adds one to <c>Writes</c> where
    /// the count is under a limit, the row is no record of a deleted copy, and the caller is a user
    /// of that copy.
    /// </summary>
    private static readonly Regex TheBudgetsStatement = new(
        @"^UPDATE \[d\]\s+SET \[d\]\.\[Writes\] = \[d\]\.\[Writes\] \+ 1\s+FROM \[DemoCopies\] AS \[d\]\s+" +
        @"WHERE \[d\]\.\[Writes\] < @\w+ AND \[d\]\.\[DeletedAt\] IS NULL AND EXISTS \(\s+SELECT 1\s+FROM \[AspNetUsers\] AS \[a\]\s+" +
        @"WHERE \[a\]\.\[Id\] = @\w+ AND \[a\]\.\[DemoCopyId\] = \[d\]\.\[Id\]\)$",
        RegexOptions.CultureInvariant);

    [SqlServerFact]
    public async Task AChange_IsCountedByOneStatementThatCarriesItsOwnCondition_OutsideAnyTransaction_AndBeforeAnythingElseIsWritten()
    {
        await using var database = await DemoPoolDatabase.CreateAsync();
        await database.BuildCopiesAsync(1);
        var demoApi = DemoApiOf(database);
        var onTheDemo = new StatementsSent();
        demoApi.AddInterceptor(onTheDemo);
        using var demoClient = demoApi.CreateClient();
        var (copy, owner, claim) = await ClaimACopyAsync(database, demoClient);
        var savings = (await owner.AccountsAsync()).Single(a => a.IsPrimary);

        // From here: what the host sent to start, to claim and to list the accounts is not the
        // deposit's.
        onTheDemo.Start();
        using var deposited = await owner.DepositAsync(savings.Id, 5m);
        var ofTheDeposit = onTheDemo.All;
        using var listed = await owner.ListAccountsAsync();
        var ofTheRead = onTheDemo.All.Skip(ofTheDeposit.Count).ToList();

        // And one more deposit, with the copy at its limit.
        await SetWritesAsync(database, copy.Id, Limit);
        var beforeTheRefusal = onTheDemo.All.Count;
        using var refused = await owner.DepositAsync(savings.Id, 5m);
        var ofTheRefusal = onTheDemo.All.Skip(beforeTheRefusal).ToList();

        deposited.StatusCode.Should().Be(HttpStatusCode.Created, await AnswerOfAsync(deposited));
        listed.StatusCode.Should().Be(HttpStatusCode.OK);
        await ShouldBeTheCopyLimitAsync(refused);
        using (new AssertionScope())
        {
            // One statement, and the limit is in it: nothing reads the count first and writes it
            // afterwards, so two changes that arrive together cannot both read "one left".
            var aboutThePool = ofTheDeposit.Where(s => Names(s.Text, "DemoCopies")).ToList();
            aboutThePool.Should().ContainSingle("a change sends one statement about the pool");
            aboutThePool.Select(s => s.Text).Should().OnlyContain(text => TheBudgetsStatement.IsMatch(text));
            aboutThePool.Should().OnlyContain(
                s => !s.InTransaction, "it commits by itself: it is not part of the deposit's transaction, and holds no lock while the deposit runs");

            // Before the idempotency claim, which is the first thing a deposit writes.
            var texts = ofTheDeposit.Select(s => s.Text).ToList();
            var budget = texts.FindIndex(t => Names(t, "DemoCopies"));
            var claimOfTheKey = texts.FindIndex(WritesAKeysClaim);
            claimOfTheKey.Should().BeGreaterThan(-1, "CONTROL: the recorder heard the deposit's own statements, the claim of its key among them");
            texts.Should().Contain(t => Names(t, "Transactions"), "CONTROL: and the ledger row");
            budget.Should().BeLessThan(claimOfTheKey, "the budget is spent before anything is written for the request");

            // A read is not counted, so it sends nothing about the pool.
            ofTheRead.Should().Contain(s => Names(s.Text, "Accounts"), "CONTROL: the recorder heard the read");
            ofTheRead.Should().NotContain(s => Names(s.Text, "DemoCopies"));

            // A change past the limit sends the budget's statement and stops there: no claim of
            // its idempotency key is written, to be taken back afterwards.
            var refusedAboutThePool = ofTheRefusal.Where(s => Names(s.Text, "DemoCopies")).Select(s => s.Text).ToList();
            refusedAboutThePool.Should().ContainSingle("the refusal is the answer to that one statement");
            refusedAboutThePool.Should().OnlyContain(text => TheBudgetsStatement.IsMatch(text));
            ofTheRefusal.Select(s => s.Text).Should().NotContain(text => WritesAKeysClaim(text), "no key is claimed for a refused change");
            ofTheRefusal.Should().NotContain(s => Names(s.Text, "Accounts") || Names(s.Text, "Transactions"), "and nothing of the deposit was started");
        }

        // Where the demo is off the same user's deposit sends nothing about the pool at all.
        var ordinaryApi = database.Api();
        var onTheOrdinary = new StatementsSent();
        ordinaryApi.AddInterceptor(onTheOrdinary);
        using var ordinaryClient = ordinaryApi.CreateClient();
        var sameUser = await DemoVisitor.SignInAsync(ordinaryClient, claim.Copy.Email, claim.Copy.Password);
        onTheOrdinary.Start();
        using var ordinaryDeposit = await sameUser.DepositAsync(savings.Id, 5m);

        ordinaryDeposit.StatusCode.Should().Be(HttpStatusCode.Created, await AnswerOfAsync(ordinaryDeposit));
        using (new AssertionScope())
        {
            onTheOrdinary.All.Should().Contain(s => Names(s.Text, "IdempotencyRecords"), "CONTROL: the recorder heard this deposit too");
            onTheOrdinary.All.Should().NotContain(s => Names(s.Text, "DemoCopies"));
            (await WritesOfAsync(database, copy.Id)).Should().Be(Limit, "the count is where the refusal left it: this deposit did not touch it");
        }
    }

    /// <summary>Sends <paramref name="count"/> requests at once and returns their answers.</summary>
    private static async Task<HttpResponseMessage[]> AtOnceAsync(HttpClient client, int count, Func<Task<HttpResponseMessage>> send)
    {
        // One request first, so that the host's first-use work is done before the others start.
        using (await client.GetAsync("/health/live"))
        {
        }

        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, count)
            .Select(_ => Task.Run(async () =>
            {
                await go.Task;
                return await send();
            }))
            .ToArray();
        go.SetResult();
        return await Task.WhenAll(requests);
    }

    [SqlServerTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangesThatArriveTogether_AreEachCountedOnce_AndStopAtTheLimit(bool rowVersioning)
    {
        const int together = 24;
        await using var database = await DemoPoolDatabase.CreateAsync();
        await DemoClaimSqlServerTests.SetRowVersioningAsync(database, rowVersioning);
        await database.BuildCopiesAsync(1);
        var api = DemoApiOf(database);

        // Room for every request to hold a connection at once: waiting for one of the test host's
        // twelve is another test's subject.
        api.SetConnectionString(new SqlConnectionStringBuilder(database.ConnectionString) { MaxPoolSize = 30 }.ConnectionString);
        api.CaptureLog();
        using var client = api.CreateClient();
        var (copy, owner, _) = await ClaimACopyAsync(database, client);

        // A PIN of two digits is refused for its shape, with nothing read or written: the budget's
        // statement is all each of these sends.
        var responses = await AtOnceAsync(client, together, () => owner.VerifyPinAsync("12"));

        var statuses = responses.Select(r => (int)r.StatusCode).Order().ToList();
        statuses.Should().Equal(
            [.. Enumerable.Repeat(400, Limit), .. Enumerable.Repeat(429, together - Limit)],
            "{0} changes at once on a copy with {1} left: {1} go on and the others are refused. The API logged: {2}",
            together, Limit, string.Join(" || ", api.CapturedLog.Where(l => l.Contains("responded 5", StringComparison.Ordinal))));
        foreach (var refused in responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests))
        {
            await ShouldBeTheCopyLimitAsync(refused);
        }

        using (new AssertionScope())
        {
            (await WritesOfAsync(database, copy.Id)).Should().Be(Limit, "the count never passes the limit, and no change that went on is missing from it");
            (await DemoClaimSqlServerTests.RowVersioningAsync(database)).Should().Be(rowVersioning, "the changes ran under the setting this row names");
        }
    }
}
