using System.Security.Cryptography;
using System.Text;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Interfaces;
using Konscious.Security.Cryptography;

namespace AzureBank.Shared.Services.Implementations;

/// <summary>
/// Argon2id hasher with two security profiles:
///
/// 1. PASSWORD Profile (High Security):
///    - Memory: 64 MB (65536 KB)
///    - Iterations: 3
///    - Use for: General password hashing, sensitive data
///
/// 2. PIN Profile (Optimized):
///    - Memory: 19 MB (19456 KB) - OWASP Tier 2
///    - Iterations: 2
///    - Use for: 6-digit PINs with limited entropy
///
/// Both profiles use:
///    - Parallelism: 4 threads
///    - Salt: 16 bytes (128 bits)
///    - Hash: 32 bytes (256 bits)
///
/// PIN PEPPER (ADR-0011): when a pepper is configured, PIN hashing/verification
/// additionally mixes in a server-side secret — Argon2id's RFC 9106 secret value
/// (K), supplied via Konscious <c>KnownSecret</c> — that is kept OUT of the
/// database. Because the secret leaves no trace in the stored PHC string, new PIN
/// hashes are stamped with a <c>keyid=N</c> parameter so verification is
/// self-describing: a hash carrying a keyid is verified WITH the matching pepper;
/// a hash without one is a legacy (un-peppered) hash verified WITHOUT a pepper and
/// upgraded on next use (see <see cref="PinNeedsRehash"/>). Account passwords are
/// handled by ASP.NET Core Identity and are intentionally out of scope here, so the
/// password profile is never peppered.
///
/// References:
/// - OWASP Password Storage Cheat Sheet (2024)
/// - RFC 9106: Argon2 Memory-Hard Function
/// </summary>
public class PasswordHasher : IPasswordHasher
{
    #region Password Profile (64 MB - High Security)

    private const int PasswordMemorySize = 65536;   // 64 MB
    private const int PasswordIterations = 3;        // Time cost

    #endregion

    #region PIN Profile (19 MB - Optimized for Limited Entropy)

    // OWASP Tier 2: Lower memory is acceptable for PINs because:
    // - 6 digits = only 1,000,000 possible combinations
    // - Memory-hardness provides less benefit for small search spaces
    // - Faster verification (~50ms vs ~300ms) without significant security loss
    private const int PinMemorySize = 19456;         // 19 MB (OWASP Tier 2)
    private const int PinIterations = 2;             // Time cost

    #endregion

    #region Shared Parameters

    private const int Parallelism = 4;               // Parallel threads
    private const int SaltLength = 16;               // 128 bits
    private const int HashLength = 32;               // 256 bits
    private const string KeyIdParam = "keyid";       // PHC param tagging the pepper key

    #endregion

    #region Pepper (ADR-0011)

    private readonly byte[]? _pinPepper;
    private readonly int _pinPepperKeyId;

    /// <summary>
    /// Creates a hasher. When <paramref name="options"/> supplies a non-empty
    /// <see cref="PinHashingOptions.PinPepper"/>, PIN hashing/verification uses it
    /// (mechanism: Argon2id <c>KnownSecret</c>) and new PIN hashes are tagged with
    /// the pepper key id. With no options — or an empty pepper — the hasher behaves
    /// exactly as the legacy un-peppered hasher (no <c>keyid</c>, no secret).
    /// </summary>
    public PasswordHasher(PinHashingOptions? options = null)
    {
        var pepper = options?.PinPepper;
        _pinPepper = string.IsNullOrEmpty(pepper) ? null : Encoding.UTF8.GetBytes(pepper);
        _pinPepperKeyId = options?.PinPepperKeyId ?? 0;
    }

    #endregion

    #region Password Methods (64 MB)

    /// <inheritdoc />
    public string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Hash(password, PasswordMemorySize, PasswordIterations, pepper: null, keyId: null);
    }

    /// <inheritdoc />
    public bool VerifyPassword(string hash, string password)
    {
        return Verify(hash, password);
    }

    #endregion

    #region PIN Methods (19 MB)

    /// <inheritdoc />
    public string HashPin(string pin)
    {
        ArgumentException.ThrowIfNullOrEmpty(pin);
        // Peppered PIN hashes are tagged with the active key id; un-peppered ones
        // stay in the legacy format so they remain recognizable as pre-pepper.
        return Hash(pin, PinMemorySize, PinIterations,
            _pinPepper, _pinPepper is null ? null : _pinPepperKeyId);
    }

    /// <inheritdoc />
    public bool VerifyPin(string hash, string pin)
    {
        return Verify(hash, pin);
    }

    /// <inheritdoc />
    public bool PinNeedsRehash(string hash)
    {
        // No pepper configured → nothing to migrate to.
        if (_pinPepper is null || string.IsNullOrEmpty(hash))
            return false;

        // A legacy hash (no keyid) or one tagged with an older key id should be
        // upgraded to the currently active pepper on next successful use.
        return TryReadKeyId(hash) != _pinPepperKeyId;
    }

    #endregion

    #region Private Implementation

    /// <summary>
    /// Core hashing implementation with configurable parameters. When
    /// <paramref name="pepper"/> is supplied it is mixed in as the Argon2id secret
    /// value (K) and <paramref name="keyId"/> is recorded in the PHC parameters.
    /// </summary>
    private static string Hash(string input, int memorySize, int iterations, byte[]? pepper, int? keyId)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(input))
        {
            Salt = salt,
            MemorySize = memorySize,
            Iterations = iterations,
            DegreeOfParallelism = Parallelism
        };
        if (pepper is not null)
        {
            argon2.KnownSecret = pepper;
        }

        var hash = argon2.GetBytes(HashLength);

        // PHC string format: $argon2id$v=19$m={memory},t={iterations},p={parallelism}[,keyid={id}]$<salt>$<hash>
        var keyIdSuffix = keyId is { } id ? $",{KeyIdParam}={id}" : string.Empty;
        return $"$argon2id$v=19$m={memorySize},t={iterations},p={Parallelism}{keyIdSuffix}$" +
               $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Core verification implementation. Reads parameters from the stored hash for
    /// forward compatibility, and applies the pepper only when the hash is tagged
    /// with a keyid we hold (self-describing pepper).
    /// </summary>
    private bool Verify(string hash, string input)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(input))
            return false;

        try
        {
            // Parse PHC string format: $argon2id$v=19$m=X,t=Y,p=Z[,keyid=K]$salt$hash
            var parts = hash.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id")
                return false;

            var parameters = ParseParams(parts[3]);
            var memory = int.Parse(parameters["m"]);
            var iterations = int.Parse(parameters["t"]);
            var parallelism = int.Parse(parameters["p"]);

            var salt = Convert.FromBase64String(parts[4]);
            var storedHash = Convert.FromBase64String(parts[5]);

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(input))
            {
                Salt = salt,
                MemorySize = memory,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };

            // Self-describing pepper: a hash tagged with a keyid was peppered, so it
            // must be verified WITH the matching pepper. A hash with a keyid we do
            // not hold cannot be verified (fail closed). No keyid = legacy = no pepper.
            if (parameters.TryGetValue(KeyIdParam, out var keyIdText))
            {
                var pepper = ResolvePepper(int.Parse(keyIdText));
                if (pepper is null)
                    return false;
                argon2.KnownSecret = pepper;
            }

            var computedHash = argon2.GetBytes(storedHash.Length);

            // Constant-time comparison to prevent timing attacks
            return CryptographicOperations.FixedTimeEquals(storedHash, computedHash);
        }
        catch
        {
            // Any parsing error means invalid hash format
            return false;
        }
    }

    /// <summary>Returns the pepper for <paramref name="keyId"/>, or null if we don't hold it.</summary>
    private byte[]? ResolvePepper(int keyId)
        => _pinPepper is not null && keyId == _pinPepperKeyId ? _pinPepper : null;

    /// <summary>Reads the <c>keyid</c> from a PHC hash, or null if legacy/malformed.</summary>
    private static int? TryReadKeyId(string hash)
    {
        var parts = hash.Split('$');
        if (parts.Length != 6)
            return null;
        return ParseParams(parts[3]).TryGetValue(KeyIdParam, out var v) && int.TryParse(v, out var k)
            ? k
            : null;
    }

    /// <summary>Parses a PHC parameter block ("m=..,t=..,p=..[,keyid=..]") into a map.</summary>
    private static Dictionary<string, string> ParseParams(string paramSegment)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in paramSegment.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
                map[part[..eq]] = part[(eq + 1)..];
        }
        return map;
    }

    #endregion
}
