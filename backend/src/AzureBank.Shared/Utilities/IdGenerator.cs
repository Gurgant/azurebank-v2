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
    /// Generates a transaction number in format TXN-YYYYMMDD-XXXXXXXXXXC (24 characters), where the
    /// final character is a check symbol over the date and the suffix — the eighteen characters
    /// that carry information. The literal <c>TXN-</c> and the two hyphens are outside it and could
    /// not be inside it: they are not in the encoding alphabet, so the residue has nothing to score
    /// them with.
    ///
    /// <para>
    /// <b>The check symbol guards a different failure from the collision work that preceded it.</b>
    /// This value is printed on a receipt and read down a phone, so the realistic thing that goes
    /// wrong is a TRANSCRIPTION TYPO, not a one-in-a-quadrillion clash. Without a check symbol a
    /// mistyped number is indistinguishable from a valid one and can resolve to a DIFFERENT real
    /// transaction — silently, which is the worst way for a bank to be wrong. The retail analogue
    /// does the same thing for the same reason: ISO 11649's RF Creditor Reference carries mod-97
    /// check digits.
    /// </para>
    /// <para>
    /// The guarantee is TOTAL rather than statistical, which is why mod 37 over a base-32 payload
    /// was chosen. 37 is prime and larger than the alphabet, so a single substituted character
    /// shifts the residue by <c>weight × (a − b) mod 37</c>, which can never be zero; swapping two
    /// adjacent characters shifts it by <c>31 × weight × (a − b) mod 37</c>, and 31, 32 and 37 are
    /// pairwise coprime, so no weight can cancel it either.
    /// <b>Every</b> substitution of one symbol for a DIFFERENT symbol, and <b>every</b> adjacent
    /// transposition of two DIFFERENT symbols, is therefore rejected — swapping two identical
    /// characters yields the same string and must stay valid — asserted exhaustively in <c>IdGeneratorTests</c>
    /// rather than argued here.
    /// </para>
    /// <para>
    /// Read "different symbol" strictly: Crockford's aliases are decoded before the check runs, so
    /// writing O where the value holds 0, or I or L where it holds 1, is <b>not</b> a substitution
    /// at all — it is the same value spelled the way a person spells it, and accepting it is the
    /// entire reason those letters are absent from the alphabet. <c>IsValidTransactionNumber</c>
    /// therefore says true for those, by design and with a test of its own.
    /// </para>
    /// <para>
    /// The suffix also goes from 7 to 10 characters. That is not the motivation; it is that a
    /// migration was unavoidable anyway, because the old format filled the column exactly (20 of
    /// 20) and the check symbol had nowhere to go. 32¹⁰ = 1,125,899,906,842,624 values per UTC day
    /// puts the 50% collision point past a million transactions a day, which retires the question
    /// instead of deferring it a third time.
    /// </para>
    /// <para>
    /// <b>Existing rows keep their older, shorter numbers and are left exactly as they are.</b>
    /// There are TWO legacy widths, not one: 19 characters before PR #89 (<c>TXN-</c> + date + six
    /// digits) and 20 between #89 and this change. #89 merged barely three hours before this, so
    /// nearly every historical row is the 19-character shape — worth stating precisely, because a
    /// future legacy branch written against "20" would silently miss almost all of them, which is
    /// the resolve-to-nothing failure the check symbol exists to prevent.
    /// There is no backfill: nothing reads this value back for validation, and
    /// <c>EnforceTransactionImmutability</c> forbids renumbering a saved transaction, so the
    /// formats coexist by design. <see cref="IsValidTransactionNumber"/> answers false for every
    /// pre-migration number — call it only on numbers minted after this change.
    /// </para>
    /// </summary>
    /// <returns>
    /// Transaction number string (e.g., "TXN-20260114-K3M9PZ2QW42"). That example is a genuinely
    /// valid number — its check symbol is computed, not decorative — and
    /// <c>IdGeneratorTests.TheExampleInTheDocComment</c> asserts it, because the previous example
    /// ended in X and would have been rejected by the very method documented here.
    /// </returns>
    public static string GenerateTransactionNumber()
    {
        var stamp = $"{DateTime.UtcNow:yyyyMMdd}";
        var suffix = RandomSuffix(TransactionSuffixLength);
        return $"TXN-{stamp}-{suffix}{CheckSymbol(stamp + suffix)}";
    }

    /// <summary>
    /// True when the argument is a well-formed transaction number whose check symbol matches its
    /// content.
    ///
    /// <para>
    /// Input is normalised the way Crockford specifies before checking, which is what makes the
    /// symbol useful on a value a human retyped: case is folded, I and L read as 1, and O reads as
    /// 0. Those letters are absent from the encoding alphabet precisely so they can be
    /// reinterpreted here instead of rejected.
    /// </para>
    /// <para>
    /// Nothing calls this on the request path today — no endpoint accepts a transaction number as
    /// input. It exists for a support tool and for the lookup-by-reference endpoint that would
    /// otherwise resolve a typo to somebody else's transaction. Stated plainly so the next reader
    /// does not assume a validation step that is not wired up.
    /// </para>
    /// </summary>
    public static bool IsValidTransactionNumber(string? transactionNumber)
    {
        if (transactionNumber is null || transactionNumber.Length != TransactionNumberFormatLength)
        {
            return false;
        }

        // ASCII ONLY, and this gate is load-bearing rather than tidy-minded. Normalisation runs
        // BEFORE the alphabet check below, so any character that invariant-uppercases INTO the
        // alphabet would be folded into a legal one and sail through. Measured, not theorised:
        // U+017F LATIN SMALL LETTER LONG S uppercases to 'S', and before this gate
        // IsValidTransactionNumber("TXN-20260809-7P6PGſHSN3~") answered TRUE while the real number
        // was "TXN-20260809-7P6PGSHSN3~" — a different string reported as the same valid number,
        // which is precisely the resolve-to-something-else failure the check symbol exists to stop.
        // Gating the whole class here beats chasing individual code points.
        if (!transactionNumber.All(char.IsAscii))
        {
            return false;
        }

        var normalised = Normalise(transactionNumber);
        if (!normalised.StartsWith("TXN-", StringComparison.Ordinal) || normalised[12] != '-')
        {
            return false;
        }

        var stamp = normalised.Substring(4, 8);
        var suffix = normalised.Substring(13, TransactionSuffixLength);

        // The stamp must be eight digits and the suffix must be encoding symbols. Without these an
        // internally-coherent string of letters would checksum correctly and pass as a number.
        if (!stamp.All(char.IsAsciiDigit) || !suffix.All(c => Base32Alphabet.Contains(c)))
        {
            return false;
        }

        return normalised[^1] == CheckSymbol(stamp + suffix);
    }

    /// <summary>Crockford base32 — the digits plus 22 letters, with I, L, O and U removed.</summary>
    private const string Base32Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Crockford's check alphabet: the 32 encoding symbols plus five — <c>*~$=U</c> — that can never
    /// appear in the payload. The extension exists to reach 37 symbols for a prime modulus, NOT to
    /// make check symbols recognisable: only residues 32–36 land on one of the five extras, so
    /// 32 of the 37 residues produce an ordinary encoding character that is indistinguishable from
    /// a payload character by class alone. Measured over 20,000 generated numbers: 17,247 (86.2%)
    /// ended in an encoding symbol, against the predicted 32/37 = 86.5%.
    /// </summary>
    private const string CheckAlphabet = Base32Alphabet + "*~$=U";

    private const int TransactionSuffixLength = 10;

    /// <summary>"TXN-" + 8 date digits + "-" + suffix + check symbol.</summary>
    private const int TransactionNumberFormatLength = 4 + 8 + 1 + TransactionSuffixLength + 1;

    /// <summary>
    /// The mod-37 residue of the payload read as a base-32 number, mapped to Crockford's check
    /// alphabet.
    ///
    /// <para>
    /// Accumulated one character at a time rather than by decoding the whole string to an integer
    /// first: eighteen base-32 characters is 32¹⁸ = 2⁹⁰ ≈ 1.24e27, which overflows <c>ulong</c> —
    /// not every fixed-width type, since <c>UInt128</c> would hold it on this TFM — and taking the
    /// modulus as it goes keeps the arithmetic exact without reaching for either that or BigInteger.
    /// The DATE is inside the payload, so a typo in the date is caught too — a check symbol over
    /// the random part alone would wave through TXN-20260114 mistyped as TXN-20260115.
    /// </para>
    /// </summary>
    private static char CheckSymbol(string payload)
    {
        // PRECONDITION, ENFORCED: every character of `payload` is in Base32Alphabet. Both call
        // sites establish it — GenerateTransactionNumber builds the payload from the alphabet, and
        // IsValidTransactionNumber reaches here only past its digit and alphabet gates — so the
        // throw below is unreachable today and the guard is for the third caller.
        //
        // It is a guard rather than a comment because of what the unguarded version actually did,
        // probed rather than reasoned about: IndexOf returns -1 for a foreign character and C# `%`
        // keeps the sign, so CheckSymbol("-") threw IndexOutOfRangeException — fine, loud — but
        // CheckSymbol("20260809ABCDEFGH-") returned '8'. A SILENTLY WRONG check symbol on a value
        // whose entire job is catching wrong values is the one outcome worth spending three lines
        // to make impossible.
        var residue = 0;
        foreach (var c in payload)
        {
            var value = Base32Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
            {
                throw new ArgumentException(
                    $"'{c}' is not a Crockford base-32 encoding symbol.", nameof(payload));
            }

            residue = ((residue * 32) + value) % 37;
        }

        return CheckAlphabet[residue];
    }

    /// <summary>Crockford decoding: fold case, and read I and L as 1 and O as 0.</summary>
    private static string Normalise(string value)
    {
        var chars = value.ToUpperInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = chars[i] switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                var other => other,
            };
        }

        return new string(chars);
    }

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
