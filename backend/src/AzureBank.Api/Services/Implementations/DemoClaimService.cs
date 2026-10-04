using System.Security.Cryptography;
using AzureBank.Api.Mappers;
using AzureBank.Api.Observability;
using AzureBank.Api.Security;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// Hands a free demo copy to a visitor: marks it claimed, gives its owner a password, and opens a
/// session as that owner, in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// ONE COPY, ONE VISITOR. A copy is taken with one conditional statement, "claim this copy if
/// nobody has": of any number of claims that reach the same free copy, the database lets exactly
/// one change the row, and the others see no row changed and try the next candidate. Nothing here
/// reads a copy as free and then writes it as claimed in two steps.
/// </para>
/// <para>
/// ALL OR NOTHING. The claim, the password and the grant commit together. A claim that fails after
/// its first statement leaves the copy free and its owner without a password, so a copy is never
/// spent on a visitor who was not answered, and never left claimed with nobody able to sign in.
/// </para>
/// <para>
/// THE AMBIGUOUS COMMIT. Everything a claim makes up, its id included, is made before the
/// transaction and once. If the answer to the commit is lost and the strategy asks whether the work
/// landed, the question is "does a copy carry this claim's id": no other request can have written
/// it. A claim that landed is then answered, not run again on a second copy.
/// </para>
/// </remarks>
public class DemoClaimService(
    AzureBankDbContext context,
    IJwtService jwtService,
    IRefreshTokenService refreshTokenService,
    Microsoft.AspNetCore.Identity.IPasswordHasher<ApplicationUser> passwordHasher,
    UserMapper userMapper,
    IOptions<DemoOptions> demo,
    TimeProvider clock,
    ILogger<DemoClaimService> logger) : IDemoClaimService
{
    /// <summary>Free copies one round reads, to try in turn.</summary>
    private const int CandidatesPerRound = 20;

    /// <summary>Rounds of candidates a claim tries before it gives up.</summary>
    private const int Rounds = 3;

    private const string OutcomeTag = "azurebank.outcome";

    // Identity's hasher takes a user and does not read it; the owner is not known yet when the
    // hash is made, outside the transaction.
    private static readonly ApplicationUser NoUser =
        new() { AzureTag = string.Empty, FirstName = string.Empty, LastName = string.Empty };

    /// <summary>A free copy as the candidates' read sees it: no more than the index of free copies holds.</summary>
    private sealed record Candidate(Guid Id, DateTime CreatedAt);

    /// <summary>The copy a claim took, and the user a visitor signs in to it as.</summary>
    private sealed record TakenCopy(Guid Id, Guid OwnerUserId);

    /// <inheritdoc />
    public async Task<DemoClaimResponse> ClaimAsync(DemoClaimRequest request, CancellationToken cancellationToken = default)
    {
        var options = demo.Value;
        if (!options.Enabled)
        {
            // The pipeline answers 404 before this while the demo is off (DemoEndpointMiddleware).
            // Refused here too, as the pool's own commands refuse: nothing hands out a copy on a
            // deployment that is not the demo, whoever calls.
            throw new InvalidOperationException("The demo is off (Demo:Enabled is false): no copy is claimed.");
        }

        // Made once, before the transaction: an attempt that is run again writes the same claim,
        // and the slow part, the password's hash, is spent while no row is locked.
        Guid? claimId = Guid.CreateVersion7();
        var password = DemoPasswordGenerator.Create();
        var passwordHash = passwordHasher.HashPassword(NoUser, password);
        var securityStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        var concurrencyStamp = Guid.NewGuid().ToString();
        var clientKey = DemoClientKey.Of(options.ClientKeySecret!, request.ClientAddress);
        var now = clock.GetUtcNow().UtcDateTime;
        DateTime? claimedAt = now;

        DemoClaimResponse? response = null;
        var copyId = Guid.Empty;

        await context.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
            async ct =>
            {
                // Each attempt decides afresh: what an earlier one read or tracked did not land.
                context.ChangeTracker.Clear();
                response = null;
                copyId = Guid.Empty;

                await RefuseAClientAtItsDailyCapAsync(clientKey, now, options.Claim.MaxPerClientPerDay, ct);

                var won = await TakeACopyAsync(claimedAt, claimId, clientKey, now, options.Pool.MaxFreeAgeHours, ct);
                copyId = won.Id;

                /*
                  The copy's two other users, by the copy and never by a handle: whom the owner can
                  pay. Sorted here, by code point, so the order is the same on every database.

                  READ BEFORE THIS CLAIM WRITES A USER, because it is the one read that can touch
                  another copy's users: the database is free to answer it by reading every user.
                  Run after the password was written, two claims at once each held their own
                  owner's row and waited to read the other's. Measured on SQL Server (LocalDB)
                  with READ_COMMITTED_SNAPSHOT off, by running
                  DemoClaimSqlServerTests.EightParallelClaims_... (eight claims at once on a pool
                  of five): three runs of three ended with claims answered 503 for error 1205
                  (three of the eight in one run, four in another). Of the 18 deadlock reports
                  the runs made with that order left in the server's system_health session
                  (xml_deadlock_report), 17 show this statement on every side, each waiting for a
                  key of PK_AspNetUsers another held.

                  Here the claim holds its pool row and no user's, and from the next statement on
                  it reads no user but its own owner, by key: a claim that holds a user's row
                  never waits for another's.
                  DemoClaimSqlServerTests.OnceAClaimHasWrittenAUser_... holds the order, and
                  EightParallelClaims_... runs it, in its row without row versioning.
                */
                var contacts = await context.Users.AsNoTracking()
                    .Where(u => u.DemoCopyId == won.Id && u.Id != won.OwnerUserId)
                    .Select(u => u.AzureTag)
                    .ToListAsync(ct);
                contacts.Sort(StringComparer.Ordinal);

                if (!await SetThePasswordAsync(won.OwnerUserId, passwordHash, securityStamp, concurrencyStamp, ct))
                {
                    // A free copy has no password: the pool's builder never sets one. This copy's
                    // owner has one, so somebody holds a password for it, and it is not handed
                    // out. On the demo the sign-in gate refuses that password while the copy is
                    // free, and lets it through once the copy is claimed with the password still
                    // in place (DemoClaimSqlServerTests: AUserOutsideEveryCopy_... for the first;
                    // WithTheDemoOff_ASignInSendsNoStatement..., whose copy is claimed by hand,
                    // for the second). (An owner that is not there at all, which no command leaves
                    // behind, ends here too.) Throwing rolls the claim back; the copy stays free
                    // until the pool's job removes it as too old.
                    logger.LogError(
                        "Demo copy {CopyId} is free and its owner has a password; the claim was rolled back", won.Id);
                    throw new InvalidOperationException("A free demo copy's owner already has a password.");
                }

                // In login's order: the stamp is read before the grant is issued, and the access
                // token is minted before the grant (AuthService.LoginAsync says why for each).
                var owner = await context.Users.AsNoTracking().SingleAsync(u => u.Id == won.OwnerUserId, ct);
                var tokenResult = jwtService.GenerateToken(owner);
                var grant = await refreshTokenService.IssueAsync(owner, ct);

                response = new DemoClaimResponse
                {
                    Token = AuthService.ToTokenResponse(tokenResult, grant, owner.SessionStamp),
                    User = userMapper.ToLoginInfo(owner),
                    Copy = new DemoCopyInfo
                    {
                        Email = owner.Email ?? string.Empty,
                        Password = password,
                        Pin = DemoCopyDefaults.Pin,
                        Contacts = contacts,
                        ExpiresAt = now.AddHours(options.CopyLifetimeHours),
                    },
                };
            },
            ct => context.DemoCopies.AsNoTracking().AnyAsync(c => c.ClaimId == claimId, ct),
            cancellationToken);

        // Only now: a line or a count written inside would stand for a claim that was rolled back.
        logger.LogInformation("Demo copy {CopyId} claimed for user {UserId}", copyId, response!.User.Id);
        Count("claimed");
        return response;
    }

    /// <summary>
    /// Refuses a client that has claimed <paramref name="cap"/> copies or more in the 24 hours
    /// before <paramref name="now"/>, and says in how long it is back under that number.
    /// </summary>
    /// <remarks>
    /// The count the pool's job reports clients at their cap by: the rows that carry this client's
    /// key and were claimed after the instant 24 hours ago. Two claims of one client at once can
    /// both pass it, so a client can end up above its cap; the wait is then to the instant the
    /// client is under it again, which is when the claim at position "count minus cap" leaves the
    /// 24 hours, oldest first. When the client is exactly at its cap that is its oldest claim.
    /// </remarks>
    private async Task RefuseAClientAtItsDailyCapAsync(byte[] clientKey, DateTime now, int cap, CancellationToken ct)
    {
        DateTime? dayAgo = now.AddHours(-24);
        var counted = await context.DemoCopies.AsNoTracking()
            .Where(c => c.ClientKey == clientKey && c.ClaimedAt > dayAgo)
            .OrderBy(c => c.ClaimedAt)
            .Select(c => c.ClaimedAt)
            .ToListAsync(ct);
        if (counted.Count < cap)
        {
            return;
        }

        var backUnderTheCapAt = counted[counted.Count - cap]!.Value.AddHours(24);
        Count("daily_limit");
        throw DemoRefusalException.DailyLimit((int)Math.Ceiling((backUnderTheCapAt - now).TotalSeconds));
    }

    /// <summary>
    /// Takes one free copy for this claim, or refuses: no copy is free, or every candidate of
    /// every round was taken by another claim first.
    /// </summary>
    private async Task<TakenCopy> TakeACopyAsync(
        DateTime? claimedAt, Guid? claimId, byte[] clientKey, DateTime now, int maxFreeAgeHours, CancellationToken ct)
    {
        for (var round = 1; round <= Rounds; round++)
        {
            var candidates = await ReadCandidatesAsync(now, maxFreeAgeHours, ct);
            if (candidates.Count == 0)
            {
                Count("pool_empty");
                throw DemoRefusalException.PoolEmpty();
            }

            foreach (var candidate in candidates)
            {
                if (await TakeIfStillFreeAsync(candidate.Id, claimedAt, claimId, clientKey, ct))
                {
                    // The owner, now that the copy is this claim's: its own row, by key.
                    var ownerId = await context.DemoCopies.AsNoTracking()
                        .Where(c => c.Id == candidate.Id)
                        .Select(c => c.OwnerUserId)
                        .SingleAsync(ct);
                    return new TakenCopy(candidate.Id, ownerId);
                }
            }
        }

        // Every candidate was free when it was read and taken when its turn came, three times
        // over: the pool is being emptied faster than this request can claim from it.
        logger.LogWarning("A demo claim lost every candidate in {Attempts} rounds", Rounds);
        Count("lost_races");
        throw DemoRefusalException.PoolEmpty();
    }

    /// <summary>
    /// The free copies to try, in the order to try them: the newest twenty, those young enough to
    /// count as free for the pool's job first, each group shuffled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A free copy older than <c>Demo:Pool:MaxFreeAgeHours</c> opens on a history that ends days
    /// ago. It stays in the pool until a run of the pool's job has replaced it, so it is handed out
    /// only when no younger copy is left: an old copy is better than none.
    /// </para>
    /// <para>
    /// Shuffled, so that claims arriving together do not all try the same copy first. From the
    /// cryptographic generator, as everything a claim draws: the order copies are handed out in
    /// should not be anybody's to predict.
    /// </para>
    /// <para>
    /// ONLY THE ID AND THE SEED INSTANT, which is all the index of free copies holds
    /// (<c>IX_DemoCopies_Free</c>: <c>CreatedAt</c>, and the key). Asked for the owner as well, the
    /// database read each candidate in two steps, its index entry and then its row, holding the
    /// first while it waited for the second; a claim taking that copy holds the row and then
    /// waits for the index entry, to remove it. Measured on SQL Server (LocalDB) with
    /// READ_COMMITTED_SNAPSHOT off, by running
    /// <c>DemoClaimSqlServerTests.TwelveParallelClaims_...</c> (twelve claims at once on a pool of
    /// twenty): in 2 runs of 36, one or two claims were answered 503 for error 1205, and the three
    /// deadlock reports those runs left in the server's system_health session
    /// (xml_deadlock_report) show this read holding a key of IX_DemoCopies_Free and waiting for one
    /// of PK_DemoCopies, against the conditional update holding that one and waiting for the other.
    /// Read from the index alone, it holds nothing while it waits.
    /// <c>DemoClaimSqlServerTests.TheCandidatesRead_...</c> holds the columns.
    /// </para>
    /// </remarks>
    private async Task<List<Candidate>> ReadCandidatesAsync(DateTime now, int maxFreeAgeHours, CancellationToken ct)
    {
        var free = await context.DemoCopies.AsNoTracking()
            .Where(c => c.ClaimedAt == null)
            .OrderByDescending(c => c.CreatedAt)
            .Take(CandidatesPerRound)
            .Select(c => new Candidate(c.Id, c.CreatedAt))
            .ToListAsync(ct);

        var freshSince = now.AddHours(-maxFreeAgeHours);
        var fresh = free.Where(c => c.CreatedAt >= freshSince).ToArray();
        var old = free.Where(c => c.CreatedAt < freshSince).ToArray();
        RandomNumberGenerator.Shuffle<Candidate>(fresh);
        RandomNumberGenerator.Shuffle<Candidate>(old);
        return [.. fresh, .. old];
    }

    /// <summary>Marks the copy claimed if nobody has, and says whether this claim was the one.</summary>
    private async Task<bool> TakeIfStillFreeAsync(
        Guid id, DateTime? claimedAt, Guid? claimId, byte[] clientKey, CancellationToken ct)
    {
        if (context.Database.IsRelational())
        {
            // The condition is in the statement: of two claims that reach the same free copy, the
            // database lets one change the row and tells the other it changed none.
            var taken = await context.DemoCopies
                .Where(c => c.Id == id && c.ClaimedAt == null)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(c => c.ClaimedAt, claimedAt)
                        .SetProperty(c => c.ClaimId, claimId)
                        .SetProperty(c => c.ClientKey, clientKey),
                    ct);
            return taken == 1;
        }

        // The EF InMemory test host has no set-based statement: the same condition, read and then
        // written. Nothing races there.
        var row = await context.DemoCopies.SingleOrDefaultAsync(c => c.Id == id && c.ClaimedAt == null, ct);
        if (row is null)
        {
            return false;
        }

        row.ClaimedAt = claimedAt;
        row.ClaimId = claimId;
        row.ClientKey = clientKey;
        await context.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Gives the copy's owner a password if it has none, and says whether it did.
    /// </summary>
    /// <remarks>
    /// Set-based, and so it writes what Identity writes with a password: the security stamp, and
    /// the concurrency stamp, which must change whenever the row does. Identity writes back every
    /// column of a user it read earlier and checks only that stamp, so without the change a copy of
    /// the owner read while the copy was free could be saved after the claim and take the password
    /// away again.
    /// </remarks>
    private async Task<bool> SetThePasswordAsync(
        Guid ownerId, string passwordHash, string securityStamp, string concurrencyStamp, CancellationToken ct)
    {
        if (context.Database.IsRelational())
        {
            var set = await context.Users
                .Where(u => u.Id == ownerId && u.PasswordHash == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(u => u.PasswordHash, passwordHash)
                        .SetProperty(u => u.SecurityStamp, securityStamp)
                        .SetProperty(u => u.ConcurrencyStamp, concurrencyStamp),
                    ct);
            return set == 1;
        }

        var owner = await context.Users.SingleOrDefaultAsync(u => u.Id == ownerId && u.PasswordHash == null, ct);
        if (owner is null)
        {
            return false;
        }

        owner.PasswordHash = passwordHash;
        owner.SecurityStamp = securityStamp;
        owner.ConcurrencyStamp = concurrencyStamp;
        await context.SaveChangesAsync(ct);
        return true;
    }

    private static void Count(string outcome) =>
        ApiMetrics.DemoClaims.Add(1, new KeyValuePair<string, object?>(OutcomeTag, outcome));
}
