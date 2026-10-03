using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Seeder.Seeders;

/// <summary>
/// The token of the command that is running, for the code that cannot be handed one. One per
/// scope: whoever starts work in a scope sets it first (<see cref="SeederOrchestrator"/> does, for
/// the seeders, and <c>DemoCopyBuilder</c>, for the demo pool's two commands).
/// </summary>
/// <remarks>
/// <para>
/// IDENTITY'S MANAGERS TAKE NO TOKEN. <c>RoleExistsAsync</c>, <c>CreateAsync</c> and
/// <c>AddToRoleAsync</c> have no such parameter: each manager hands its store the value of one
/// property, <c>CancellationToken</c>, and outside a web request that value is "none". So the
/// token a command was given stopped at the first call into Identity. Measured 2026-10-01 in the
/// tools image, SQL Server stopped, <c>seed</c> told to stop five seconds in: three runs of three
/// went on retrying the open and were killed, exit 137 and not a line from the command, where
/// <c>migrate</c> and <c>reset</c> printed their line and exited 1.
/// </para>
/// <para>
/// The two managers below override that property to read this object, and
/// <c>AddSeederServices</c> registers them, so every call a seeder makes into Identity carries the
/// run's token. A command that reaches Identity without the orchestrator sets
/// <see cref="Token"/> on its scope before it resolves a manager.
/// </para>
/// </remarks>
public sealed class RunCancellation
{
    /// <summary>The run's token; "none" until it is set.</summary>
    public CancellationToken Token { get; set; }
}

/// <summary>Identity's user manager, with the run's token for every call to the store.</summary>
public sealed class CancellableUserManager(
    RunCancellation run,
    IUserStore<ApplicationUser> store,
    IOptions<IdentityOptions> optionsAccessor,
    IPasswordHasher<ApplicationUser> passwordHasher,
    IEnumerable<IUserValidator<ApplicationUser>> userValidators,
    IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<ApplicationUser>> logger)
    : UserManager<ApplicationUser>(
        store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
{
    /// <inheritdoc />
    protected override CancellationToken CancellationToken => run.Token;
}

/// <summary>Identity's role manager, with the run's token for every call to the store.</summary>
public sealed class CancellableRoleManager(
    RunCancellation run,
    IRoleStore<IdentityRole<Guid>> store,
    IEnumerable<IRoleValidator<IdentityRole<Guid>>> roleValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    ILogger<RoleManager<IdentityRole<Guid>>> logger)
    : RoleManager<IdentityRole<Guid>>(store, roleValidators, keyNormalizer, errors, logger)
{
    /// <inheritdoc />
    protected override CancellationToken CancellationToken => run.Token;
}
