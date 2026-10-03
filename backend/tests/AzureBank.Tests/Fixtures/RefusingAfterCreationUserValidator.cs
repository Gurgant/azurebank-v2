using System.Collections.Concurrent;
using AzureBank.Shared.Entities;
using Microsoft.AspNetCore.Identity;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// An Identity validator that lets a user be created and refuses every later change to it: a
/// stand-in for an Identity call that ANSWERS a failure where another would throw one.
/// </summary>
/// <remarks>
/// <para>
/// <c>UserManager</c> validates a user each time it saves it. On the way to a demo copy that is
/// twice: when the row is created, and when the user is given its role, because
/// <c>AddToRoleAsync</c> ends in an update of the user. The second validation is refused here, so
/// <c>AddToRoleAsync</c> returns a failed <c>IdentityResult</c> and throws nothing. A caller that
/// does not read the result goes on as if the role had been given.
/// </para>
/// <para>
/// Registered BESIDE Identity's own validator, after the Seeder's services: <c>UserManager</c> runs
/// every validator it is given.
/// </para>
/// </remarks>
public sealed class RefusingAfterCreationUserValidator : IUserValidator<ApplicationUser>
{
    /// <summary>The code of every injected refusal, and the first word of its description.</summary>
    public const string Code = "InjectedRefusal";

    private readonly ConcurrentDictionary<Guid, int> _validations = new();
    private int _refusals;

    /// <summary>Validations this validator refused. 0 means the test proved nothing.</summary>
    public int Refusals => Volatile.Read(ref _refusals);

    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
    {
        if (_validations.AddOrUpdate(user.Id, 1, (_, seen) => seen + 1) == 1)
        {
            return Task.FromResult(IdentityResult.Success);
        }

        Interlocked.Increment(ref _refusals);
        return Task.FromResult(IdentityResult.Failed(
            new IdentityError { Code = Code, Description = $"{Code}: Identity would not change this user." }));
    }
}
