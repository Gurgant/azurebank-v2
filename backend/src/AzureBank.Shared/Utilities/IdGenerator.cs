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
    /// Generates a transaction number in format TXN-YYYYMMDD-XXXXXXX (20 characters).
    ///
    /// <para>
    /// <b>The suffix is 7 Crockford base32 characters, not 6 digits, and that is a correctness
    /// fix rather than a cosmetic one.</b> <c>TransactionNumber</c> carries a UNIQUE index, and the
    /// date prefix means the random part only has to be unique within one UTC day. Six digits give
    /// 900,000 values per day, so by the birthday bound the odds of a collision reach 50% at
    /// roughly 1.18 × √900000 ≈ <b>1,117 transactions in a day</b> — a volume a real bank passes
    /// before breakfast. A collision is not a cosmetic clash: the INSERT violates the index, and
    /// nothing on the deposit / withdraw / transfer paths catches <c>DbUpdateException</c>, so it
    /// escapes as a 500 on a money endpoint with the idempotency record left mid-flight.
    /// </para>
    /// <para>
    /// 32⁷ = 34,359,738,368 values per day moves that 50% point to about <b>218,000 transactions a
    /// day</b>, and at a realistic 1,000/day the odds are roughly 1 in 69,000 per day. The alphabet
    /// is Crockford's: no I, L, O or U, so nothing in a "human-readable transaction ID" can be
    /// misread as a 1 or a 0 when someone types it into a support form.
    /// </para>
    /// <para>
    /// Seven characters is exactly the room already available:
    /// <c>ValidationRules.TransactionNumberLength</c> is 20 and the old format used 19, so the
    /// column is unchanged and <b>no migration is required</b>. Existing rows stay valid — nothing
    /// parses this string, it is only displayed and compared.
    /// </para>
    /// <para>
    /// No duplicate-key retry was added on top. After the widening a collision is a once-in-decades
    /// event at this application's scale, and a retry would have to sit inside the transfer's
    /// explicit transaction and execution strategy, where it could mask a genuine unique violation
    /// (the idempotency claim uses one as its distributed lock). Widening removes the cause;
    /// retrying would only soften a symptom that no longer occurs.
    /// </para>
    /// </summary>
    /// <returns>Transaction number string (e.g., "TXN-20260114-K3M9PZ2").</returns>
    public static string GenerateTransactionNumber() =>
        $"TXN-{DateTime.UtcNow:yyyyMMdd}-{RandomSuffix(TransactionSuffixLength)}";

    /// <summary>Crockford base32 — the digits plus 22 letters, with I, L, O and U removed.</summary>
    private const string Base32Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private const int TransactionSuffixLength = 7;

    private static string RandomSuffix(int length)
    {
        var suffix = new char[length];
        for (var i = 0; i < length; i++)
        {
            // GetInt32 rejection-samples internally, so every character is uniform. Doing this with
            // a byte and a modulo would bias the first 8 letters of the alphabet.
            suffix[i] = Base32Alphabet[RandomNumberGenerator.GetInt32(Base32Alphabet.Length)];
        }

        return new string(suffix);
    }

    /// <summary>
    /// Generates a transfer reference in format TRF-YYYYMMDD-XXXXXX.
    ///
    /// <para>
    /// Deliberately left at six digits while <c>GenerateTransactionNumber</c> was widened, because
    /// the reason to widen does not apply here: this value carries <b>no unique index</b>, so a
    /// repeat is a cosmetic coincidence rather than a failed INSERT. If a uniqueness constraint is
    /// ever added, this needs the same treatment first.
    /// </para>
    /// </summary>
    /// <returns>Transfer reference string (e.g., "TRF-20260114-123456").</returns>
    public static string GenerateTransferReference()
    {
        var random = RandomNumberGenerator.GetInt32(100000, 1000000);
        return $"TRF-{DateTime.UtcNow:yyyyMMdd}-{random}";
    }
}
