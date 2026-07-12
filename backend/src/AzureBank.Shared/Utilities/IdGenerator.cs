using System.Security.Cryptography;

namespace AzureBank.Shared.Utilities;

/// <summary>
/// Thread-safe utility for generating unique identifiers.
/// Uses cryptographically-secure random number generation.
/// </summary>
public static class IdGenerator
{
    /// <summary>
    /// Generates a unique account number in format AB-XXXX-XXXX-XX.
    /// Thread-safe and cryptographically secure.
    /// </summary>
    /// <returns>Account number string (e.g., "AB-1234-5678-90")</returns>
    public static string GenerateAccountNumber()
    {
        var part1 = RandomNumberGenerator.GetInt32(1000, 10000);
        var part2 = RandomNumberGenerator.GetInt32(1000, 10000);
        var part3 = RandomNumberGenerator.GetInt32(10, 100);
        return $"AB-{part1}-{part2}-{part3}";
    }

    /// <summary>
    /// Generates a unique transaction number in format TXN-YYYYMMDD-XXXXXX.
    /// Thread-safe and cryptographically secure.
    /// </summary>
    /// <returns>Transaction number string (e.g., "TXN-20260114-123456")</returns>
    public static string GenerateTransactionNumber()
    {
        var random = RandomNumberGenerator.GetInt32(100000, 1000000);
        return $"TXN-{DateTime.UtcNow:yyyyMMdd}-{random}";
    }

    /// <summary>
    /// Generates a unique transfer reference in format TRF-YYYYMMDD-XXXXXX.
    /// Thread-safe and cryptographically secure.
    /// </summary>
    /// <returns>Transfer reference string (e.g., "TRF-20260114-123456")</returns>
    public static string GenerateTransferReference()
    {
        var random = RandomNumberGenerator.GetInt32(100000, 1000000);
        return $"TRF-{DateTime.UtcNow:yyyyMMdd}-{random}";
    }
}
