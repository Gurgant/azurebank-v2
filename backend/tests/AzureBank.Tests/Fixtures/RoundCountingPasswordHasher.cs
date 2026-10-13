using System.Security.Cryptography;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Services.Implementations;
using Microsoft.AspNetCore.Identity;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// The hasher of account passwords, counting the rounds each verification spends: the rounds the
/// stored hash says it was made with, which Identity runs to verify it, and the rounds the hasher
/// spends on top.
/// </summary>
/// <remarks>
/// <para>
/// It counts work, not time: a clock on a busy machine cannot tell 100,000 rounds from 220,000
/// reliably, and the count can.
/// </para>
/// <para>
/// The rounds on top are counted only when what the hasher derived is what that many rounds of
/// PBKDF2-HMAC-SHA512 give: a number that was asked for and not run counts as nothing.
/// </para>
/// <para>
/// <see cref="BeforeTheNextAnswer"/> runs inside a verification, which in a request is after the
/// user was loaded and before anything is written for it: what another request does to that user
/// at that moment can be done there, at a known point and with no race.
/// </para>
/// </remarks>
internal sealed class RoundCountingPasswordHasher : AccountPasswordHasher
{
    private readonly Lock _gate = new();
    private readonly List<(int Stored, int OnTop)> _verifications = [];
    private int _onTop;
    private Action? _beforeTheNextAnswer;

    /// <summary>
    /// What the next verification runs, once, after it has verified and before it answers. A
    /// verification that it starts itself does not run it again.
    /// </summary>
    public void BeforeTheNextAnswer(Action run) => Volatile.Write(ref _beforeTheNextAnswer, run);

    /// <summary>Each verification so far, in order.</summary>
    public IReadOnlyList<(int Stored, int OnTop)> Verifications
    {
        get
        {
            lock (_gate)
            {
                return [.. _verifications];
            }
        }
    }

    /// <summary>Forgets the verifications counted so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _verifications.Clear();
        }
    }

    public override PasswordVerificationResult VerifyHashedPassword(
        ApplicationUser user, string hashedPassword, string providedPassword)
    {
        PasswordVerificationResult result;
        lock (_gate)
        {
            _onTop = 0;
            result = base.VerifyHashedPassword(user, hashedPassword, providedPassword);
            _verifications.Add((IterationsOf(hashedPassword), _onTop));
        }

        // Outside the gate: what it runs may sign in, and that verification takes the gate.
        Interlocked.Exchange(ref _beforeTheNextAnswer, null)?.Invoke();
        return result;
    }

    protected override byte[] TopUp(string password, int iterations)
    {
        var derived = base.TopUp(password, iterations);
        var thatManyRounds = Rfc2898DeriveBytes.Pbkdf2(
            password, new byte[16], iterations, HashAlgorithmName.SHA512, outputLength: 32);

        if (derived.AsSpan().SequenceEqual(thatManyRounds))
        {
            lock (_gate)
            {
                _onTop += iterations;
            }
        }

        return derived;
    }
}
