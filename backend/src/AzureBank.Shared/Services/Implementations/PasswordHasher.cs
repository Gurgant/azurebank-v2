using System.Security.Cryptography;
using System.Text;
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

    #endregion

    #region Password Methods (64 MB)

    /// <inheritdoc />
    public string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Hash(password, PasswordMemorySize, PasswordIterations);
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
        return Hash(pin, PinMemorySize, PinIterations);
    }

    /// <inheritdoc />
    public bool VerifyPin(string hash, string pin)
    {
        return Verify(hash, pin);
    }

    #endregion

    #region Private Implementation

    /// <summary>
    /// Core hashing implementation with configurable parameters.
    /// </summary>
    private static string Hash(string input, int memorySize, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);

        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(input))
        {
            Salt = salt,
            MemorySize = memorySize,
            Iterations = iterations,
            DegreeOfParallelism = Parallelism
        };

        var hash = argon2.GetBytes(HashLength);

        // PHC string format: $argon2id$v=19$m={memory},t={iterations},p={parallelism}$<salt>$<hash>
        return $"$argon2id$v=19$m={memorySize},t={iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Core verification implementation.
    /// Reads parameters from stored hash for forward compatibility.
    /// </summary>
    private static bool Verify(string hash, string input)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(input))
            return false;

        try
        {
            // Parse PHC string format: $argon2id$v=19$m=X,t=Y,p=Z$salt$hash
            var parts = hash.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id")
                return false;

            // Parse parameters from stored hash (supports any valid parameters)
            var paramParts = parts[3].Split(',');
            var memory = int.Parse(paramParts[0][2..]);      // Skip "m="
            var iterations = int.Parse(paramParts[1][2..]);  // Skip "t="
            var parallelism = int.Parse(paramParts[2][2..]); // Skip "p="

            var salt = Convert.FromBase64String(parts[4]);
            var storedHash = Convert.FromBase64String(parts[5]);

            using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(input))
            {
                Salt = salt,
                MemorySize = memory,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };

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

    #endregion
}
