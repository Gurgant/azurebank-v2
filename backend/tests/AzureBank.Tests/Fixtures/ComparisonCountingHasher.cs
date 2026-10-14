using AzureBank.Shared.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace AzureBank.Tests.Fixtures;

/// <summary>Counts the comparisons with the stored PIN; everything else is the API's own hasher.</summary>
/// <remarks>
/// The answers of a burst cannot tell a PIN that was refused unread from one that was compared and
/// then refused by the lock: both are 429. The count taken here can.
/// </remarks>
internal sealed class ComparisonCountingHasher : IPasswordHasher
{
    private int _comparisons;
    private int _matchedAt;

    public IPasswordHasher? Inner { get; set; }

    public int Comparisons => Volatile.Read(ref _comparisons);

    /// <summary>Which comparison, counted from 1 as they began, first matched; 0 when none did.</summary>
    public int MatchedAt => Volatile.Read(ref _matchedAt);

    public void Reset()
    {
        Volatile.Write(ref _comparisons, 0);
        Volatile.Write(ref _matchedAt, 0);
    }

    /// <summary>Puts this hasher in front of the one <paramref name="services"/> registers.</summary>
    public void InstallIn(IServiceCollection services)
    {
        var real = services.Single(d => d.ServiceType == typeof(IPasswordHasher));
        services.Remove(real);
        services.AddSingleton<IPasswordHasher>(provider =>
        {
            Inner = (IPasswordHasher)real.ImplementationFactory!(provider);
            return this;
        });
    }

    public string HashPin(string pin) => Inner!.HashPin(pin);

    public bool VerifyPin(string hash, string pin)
    {
        var number = Interlocked.Increment(ref _comparisons);
        var matched = Inner!.VerifyPin(hash, pin);
        if (matched)
        {
            Interlocked.CompareExchange(ref _matchedAt, number, 0);
        }
        return matched;
    }

    public bool PinNeedsRehash(string hash) => Inner!.PinNeedsRehash(hash);

    public bool PinPepperMissingFor(string hash) => Inner!.PinPepperMissingFor(hash);
}
