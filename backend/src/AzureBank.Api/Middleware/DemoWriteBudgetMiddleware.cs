using System.Security.Claims;
using AzureBank.Api.Attributes;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AzureBank.Api.Middleware;

/// <summary>
/// Bounds what one demo copy can change. While <c>Demo:Enabled</c> is true, each request of a
/// signed-in user that could change something is counted on the copy that user belongs to, and the
/// one past <c>Demo:Copy:MaxWrites</c> is refused with 429 <c>DEMO_COPY_LIMIT</c> before anything
/// behind the middleware runs.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is counted.</b> A request that reached an endpoint, from a signed-in user, whose method
/// is not GET, HEAD, OPTIONS or TRACE, or whose endpoint carries
/// <see cref="CountedAsDemoWriteAttribute"/>: the reveal of an account number, a GET that writes an
/// audit row. It is counted before it is looked at, so a request the API then refuses has spent
/// one all the same: a wrong PIN, a deposit with no idempotency key or with an amount the API does
/// not take (<c>DemoWriteBudgetSqlServerTests.ARequestTheApiRefuses_IsCountedToo</c>), a method
/// the path does not take
/// (<c>DemoWriteBudgetEndpointTests.WithTheDemoOn_AnotherMethodOnTheRevealsPath_...</c>). So has a
/// retry that is answered from the idempotency store
/// (<c>DemoWriteBudgetSqlServerTests.AChangeSentAgainUnderItsKey_...</c>). Each of these counts a
/// change that was not made; none lets a change through uncounted.
/// </para>
/// <para>
/// <b>What is not.</b> A request from nobody. A request that matched no endpoint. A token endpoint
/// (<see cref="TokenEndpointAttribute"/>): those are the BFF's own requests, and the one a
/// signed-in user makes, logout, signs the user out of every session, which no cap of the demo's
/// may refuse. And every request while the demo is off: the middleware then sends nothing
/// (<c>DemoWriteBudgetMiddlewareTests.WithTheDemoOff_OrASafeMethod_OrNoUser_...</c>).
/// </para>
/// <para>
/// <b>One statement.</b> The limit is in the statement that adds the one: "add one to the
/// caller's copy if it is under its limit". Nothing reads the count and writes it back in two
/// steps, so of several changes that arrive together exactly as many go on as the copy has left
/// (<c>DemoWriteBudgetSqlServerTests.ChangesThatArriveTogether_...</c>). The statement finds the
/// copy through the caller: an access token carries the user's id and no copy.
/// </para>
/// <para>
/// <b>Its own commit.</b> The statement runs outside any transaction, before the idempotency claim
/// and before the endpoint: it is no part of what the request then does, and it is not given back
/// when the request fails (<c>DemoWriteBudgetSqlServerTests.AChange_IsCountedByOneStatement...</c>).
/// </para>
/// <para>
/// <b>No copy, no budget.</b> A caller who belongs to no copy, or to the record of a deleted one,
/// has nothing to count on, and is refused as a copy at its limit is
/// (<c>DemoWriteBudgetMiddlewareTests.ACallerWithNoCopyToCountOn_...</c>). On the demo a session is
/// opened only for the owner of a claimed copy: by the claim, or by a sign-in the gate lets
/// through.
/// </para>
/// <para>
/// <b>The flag and the limit are read once,</b> when the pipeline is built, as
/// <see cref="DemoEndpointMiddleware"/> reads the flag: both are a deployment's settings, checked
/// at start.
/// </para>
/// </remarks>
public sealed class DemoWriteBudgetMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _demoEnabled;
    private readonly int _maxWrites;

    public DemoWriteBudgetMiddleware(RequestDelegate next, IOptions<DemoOptions> demo)
    {
        _next = next;
        _demoEnabled = demo.Value.Enabled;
        _maxWrites = demo.Value.Copy.MaxWrites;
    }

    // The context is scoped (the request's own), so it is resolved per request, by method
    // injection.
    public async Task InvokeAsync(HttpContext context, AzureBankDbContext db)
    {
        if (IsCounted(context) && !await CountOneAsync(db, ReadUserId(context), context.RequestAborted))
        {
            throw DemoRefusalException.CopyLimit();
        }

        await _next(context);
    }

    private bool IsCounted(HttpContext context)
    {
        if (!_demoEnabled || context.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var metadata = context.GetEndpoint()?.Metadata;
        if (metadata is null || metadata.GetMetadata<TokenEndpointAttribute>() is not null)
        {
            return false;
        }

        return !IsSafe(context.Request.Method) || metadata.GetMetadata<CountedAsDemoWriteAttribute>() is not null;
    }

    private static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    /// <summary>
    /// Adds one to the changes of the copy <paramref name="userId"/> belongs to, if that copy is
    /// under its limit, and says whether it did.
    /// </summary>
    private async Task<bool> CountOneAsync(AzureBankDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var theCallersCopyUnderItsLimit = db.DemoCopies.Where(c =>
            c.Writes < _maxWrites
            && c.DeletedAt == null
            && db.Users.Any(u => u.Id == userId && u.DemoCopyId == c.Id));

        if (db.Database.IsRelational())
        {
            var counted = await theCallersCopyUnderItsLimit.ExecuteUpdateAsync(
                set => set.SetProperty(c => c.Writes, c => c.Writes + 1), cancellationToken);
            return counted != 0;
        }

        // The EF InMemory test host has no set-based statement: the same condition, read and then
        // written. Nothing races there.
        var copy = await theCallersCopyUnderItsLimit.SingleOrDefaultAsync(cancellationToken);
        if (copy is null)
        {
            return false;
        }

        copy.Writes += 1;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static Guid ReadUserId(HttpContext context)
    {
        // The same claim resolution as the controllers (GetCurrentUserId) and as
        // IdempotencyMiddleware, with the same refusal.
        var claim = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.FindFirst("sub")?.Value;

        if (claim is null || !Guid.TryParse(claim, out var userId))
        {
            // Unreachable behind [Authorize]; defensive for misconfiguration.
            throw new AuthenticationException(
                "A valid user identity is required.", ErrorCodes.TokenInvalid);
        }

        return userId;
    }
}

/// <summary>
/// Extension method for registering the middleware.
/// </summary>
public static class DemoWriteBudgetMiddlewareExtensions
{
    /// <summary>
    /// Counts a demo copy's changes and refuses the one past its limit. After authentication,
    /// which says who the caller is; before idempotency, so nothing is written for a refused
    /// request.
    /// </summary>
    public static IApplicationBuilder UseDemoWriteBudget(this IApplicationBuilder app) =>
        app.UseMiddleware<DemoWriteBudgetMiddleware>();
}
