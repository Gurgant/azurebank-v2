using AzureBank.Shared.Utilities;
using FluentAssertions;

namespace AzureBank.Tests.Unit.Utilities;

/// <summary>
/// Unit tests for IdGenerator utility class.
/// Tests thread safety, uniqueness, and format of generated IDs.
/// </summary>
public class IdGeneratorTests
{
    #region GenerateAccountNumber Tests

    [Fact]
    public void GenerateAccountNumber_ReturnsValidFormat()
    {
        // Act
        var accountNumber = IdGenerator.GenerateAccountNumber();

        // Assert
        accountNumber.Should().MatchRegex(@"^AB-\d{4}-\d{4}-\d{2}$");
    }

    [Fact]
    public void GenerateAccountNumber_StartsWithPrefix()
    {
        // Act
        var accountNumber = IdGenerator.GenerateAccountNumber();

        // Assert
        accountNumber.Should().StartWith("AB-");
    }

    [Fact]
    public void GenerateAccountNumber_HasCorrectLength()
    {
        // Act
        var accountNumber = IdGenerator.GenerateAccountNumber();

        // Assert - "AB-1234-5678-90" = 15 characters
        accountNumber.Should().HaveLength(15);
    }

    [Fact]
    public void GenerateAccountNumber_GeneratesUniqueNumbers()
    {
        // Arrange
        const int count = 1000;

        // Act
        var numbers = Enumerable.Range(0, count)
            .Select(_ => IdGenerator.GenerateAccountNumber())
            .ToHashSet();

        /*
          AccountNumber has 9000 × 9000 × 90 = 7.29e9 values and, unlike the transaction number, no
          date prefix — so the space is shared by every account ever opened rather than reset daily.
          Even odds of a collision arrive around 100,000 accounts, which is far away but NOT
          unreachable, and the column is exactly full at 15 characters so widening it would need a
          migration. Recorded rather than fixed here.

          The tolerance drops from ten duplicates to one. Not to zero: 1,000 draws from 7.29e9
          expect 6.9e-5 duplicates, so a zero-duplicate assertion would fail against a CORRECT
          generator about once in 14,500 runs. I had computed that number, written it in a comment,
          and shipped the assertion anyway — documenting a known flake is not the same as not
          having one, and a reviewer was right to say so.

          One duplicate makes a false red a 1-in-425-million event (P(>=2) ~ 2.3e-9) while still
          failing instantly against anything meaningfully worse: the old ten-duplicate tolerance
          accepted a generator four orders of magnitude weaker than this one.
        */
        numbers.Should().HaveCountGreaterThan(
            count - 2,
            "AccountNumber is unique-indexed too, so more than one duplicate in 1,000 is a defect");
    }

    [Fact]
    public void GenerateAccountNumber_IsThreadSafe()
    {
        // Arrange
        var exceptions = new List<Exception>();
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Act - Generate in parallel
        Parallel.For(0, 100, _ =>
        {
            try
            {
                results.Add(IdGenerator.GenerateAccountNumber());
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert - No exceptions means thread-safe
        exceptions.Should().BeEmpty();
        results.Should().HaveCount(100);
    }

    #endregion

    #region GenerateTransactionNumber Tests

    [Fact]
    public void GenerateTransactionNumber_ContainsDatePrefix()
    {
        // Act
        var txnNumber = IdGenerator.GenerateTransactionNumber();

        // Assert
        var expectedDate = DateTime.UtcNow.ToString("yyyyMMdd");
        txnNumber.Should().StartWith($"TXN-{expectedDate}");
    }

    [Fact]
    public void GenerateTransactionNumber_ReturnsValidFormat()
    {
        // Act
        var txnNumber = IdGenerator.GenerateTransactionNumber();

        /*
          Format: TXN-YYYYMMDD-{10 Crockford base32}{check symbol}.

          Crockford base32 excludes I, L, O and U, so nothing in a number a human retypes can be
          confused with 1 or 0. The final character comes from the CHECK alphabet, which is those 32
          symbols plus `*~$=U` — five that can never appear in the payload, so the check symbol is
          always distinguishable from the value it protects.

          This assertion, not the sampling one below, is what pins the entropy: 32^10 is a property
          of the SUFFIX LENGTH, and a character count can be verified exactly where a collision
          count can only be estimated.
        */
        txnNumber.Should().MatchRegex(@"^TXN-[0-9]{8}-[0-9A-HJKMNP-TV-Z]{10}[0-9A-HJKMNP-TV-Z*~$=U]$");
    }

    [Fact]
    public void GenerateTransactionNumber_HasCorrectLength()
    {
        // Act
        var txnNumber = IdGenerator.GenerateTransactionNumber();

        /*
          "TXN-" + 8 date digits + "-" + 10 suffix + 1 check symbol = 24, which is
          ValidationRules.TransactionNumberLength and the width of the column after
          WidenTransactionNumberForCheckSymbol. The previous format filled the old 20-character
          column exactly, so the check symbol had nowhere to go without the migration — this is the
          arithmetic that made it unavoidable, asserted rather than left in a comment.
        */
        txnNumber.Should().HaveLength(24);
    }

    [Fact]
    public void GenerateTransactionNumber_GeneratesUniqueNumbers()
    {
        /*
          READ WHAT THIS TEST CAN AND CANNOT SEE, because the answer changed with the suffix and
          leaving the old claim in place would be the more comfortable lie.

          At 32^10 per UTC day, 20,000 draws expect 1.8e-7 duplicates. So this assertion no longer
          detects a shortened suffix at all: reverting to 7 characters expects 0.0058 duplicates and
          would sail through, and even 6 characters (0.19 expected duplicates) comes back clean in
          ~83% of runs. Sampling stopped being a usable oracle the moment the space got this large —
          no sample size that runs in a unit test can distinguish 32^7 from 32^10.

          The ENTROPY is therefore pinned by the format assertion above, which counts suffix
          characters exactly. What survives here is a different and still-worth-having property: that
          the generator actually draws randomly. A constant return, a seed shared across calls, a
          non-thread-safe accumulator — all of those collapse the output and this catches them
          immediately. Kept as an RNG smoke test, described as one.
        */
        const int count = 20_000;

        // Act
        var numbers = Enumerable.Range(0, count)
            .Select(_ => IdGenerator.GenerateTransactionNumber())
            .ToHashSet();

        /*
          ZERO duplicates now, where the previous suffix needed a tolerance of one.

          That tolerance was not a style choice: at 32^7 a zero-duplicate assertion was FLAKY
          AGAINST A CORRECT GENERATOR, failing about one run in 172, and shipping a known flake is
          how a suite starts costing people evenings. At 32^10 the expected duplicate count is
          1.8e-7, so a false red is a one-in-5.6-million event and the tolerance can go — a
          duplicate here now means the RNG collapsed, not that probability happened.

          Uniqueness itself is not a property of the generator and is not asserted here. It belongs
          to the database; TransactionNumberUniquenessSqlServerTests proves the index enforces it.
        */
        numbers.Should().HaveCount(
            count,
            "at 32^10 per day a duplicate in 20,000 draws means the generator stopped drawing randomly");
    }

    [Fact]
    public void GenerateTransactionNumber_IsThreadSafe()
    {
        // Arrange
        var exceptions = new List<Exception>();
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Act - Generate in parallel
        Parallel.For(0, 100, _ =>
        {
            try
            {
                results.Add(IdGenerator.GenerateTransactionNumber());
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // Assert - No exceptions means thread-safe
        exceptions.Should().BeEmpty();
        results.Should().HaveCount(100);
    }

    #endregion

    #region Check symbol

    /*
      The check symbol makes a claim that is TOTAL, not statistical: mod 37 over a base-32 payload
      rejects EVERY single-character substitution and EVERY adjacent transposition. A claim of that
      shape should not be defended by three hand-picked examples — the whole point of choosing a
      prime modulus larger than the alphabet is that no case escapes, so the tests below enumerate
      the cases instead of sampling them.

      Every mutant is asserted to be STRUCTURALLY WELL-FORMED before it is asserted to be rejected.
      Without that, a mutation that broke the format (a letter in the date, a check symbol out of
      alphabet) would be turned away by the format gate and the test would pass while proving
      nothing about the arithmetic — the wrong-reason pass this suite has been bitten by repeatedly.
      The guard is a regex, an oracle independent of the production check rather than a copy of it.
    */

    private const string Base32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const string CheckSymbols = Base32 + "*~$=U";
    private const string WellFormed = @"^TXN-[0-9]{8}-[0-9A-HJKMNP-TV-Z]{10}[0-9A-HJKMNP-TV-Z*~$=U]$";

    /// <summary>Date digits (4..11) followed by suffix symbols (13..22) — what the symbol covers.</summary>
    private static string Payload(string number) => number.Substring(4, 8) + number.Substring(13, 10);

    [Fact]
    public void EveryGeneratedNumberValidates()
    {
        // The floor: whatever else the symbol rejects, it must never reject a number the generator
        // just minted. 500 draws rather than one, because a bug in the residue that only bites for
        // particular payloads would hide behind a single example.
        for (var i = 0; i < 500; i++)
        {
            var number = IdGenerator.GenerateTransactionNumber();
            IdGenerator.IsValidTransactionNumber(number).Should().BeTrue(
                "a freshly generated number must validate, and {0} did not", number);
        }
    }

    [Fact]
    public void EverySingleCharacterSubstitutionIsRejected()
    {
        var exercised = 0;

        for (var draw = 0; draw < 25; draw++)
        {
            var number = IdGenerator.GenerateTransactionNumber();

            for (var position = 0; position < number.Length - 1; position++)
            {
                // "TXN-" and both hyphens are structure, not payload: mutating them is caught by the
                // format gate, so including them would inflate the count with wrong-reason passes.
                if (position < 4 || position == 12)
                {
                    continue;
                }

                // Substitute like-for-like — a digit for a date digit, an encoding symbol for a
                // suffix symbol — so the mutant stays well-formed and only the residue can reject it.
                var alphabet = position <= 11 ? "0123456789" : Base32;

                foreach (var replacement in alphabet)
                {
                    if (replacement == number[position])
                    {
                        continue;
                    }

                    var mutant = string.Concat(
                        number.AsSpan(0, position), replacement.ToString(), number.AsSpan(position + 1));

                    mutant.Should().MatchRegex(WellFormed,
                        "the mutation must stay well-formed or the format gate, not the check symbol, does the rejecting");
                    IdGenerator.IsValidTransactionNumber(mutant).Should().BeFalse(
                        "{0} is {1} with one character changed and must not validate", mutant, number);
                    exercised++;
                }
            }
        }

        // 8 date positions × 9 other digits + 10 suffix positions × 31 other symbols = 382 per draw.
        // Asserted so a loop that silently stops iterating fails instead of passing vacuously.
        exercised.Should().Be(25 * 382);
    }

    [Fact]
    public void EveryAdjacentTranspositionIsRejected()
    {
        var exercised = 0;
        var expected = 0;
        var suffixCheckPairsRun = 0;

        for (var draw = 0; draw < 25; draw++)
        {
            var number = IdGenerator.GenerateTransactionNumber();

            // 7 pairs inside the date + 9 inside the suffix, plus the suffix/check pair whenever the
            // check symbol is an encoding symbol (32 of the 37 residues). Counted from the number's
            // shape rather than from the generator below, so a loop that stops early still fails.
            var suffixCheckPairApplies = Base32.Contains(number[^1]);
            expected += 16 + (suffixCheckPairApplies ? 1 : 0);
            suffixCheckPairsRun += suffixCheckPairApplies ? 1 : 0;

            foreach (var (left, right) in AdjacentPayloadPairs(number))
            {
                var swapped = Swap(number, left, right);
                swapped.Should().MatchRegex(WellFormed, "a swap inside one region cannot change the shape");
                exercised++;

                if (number[left] == number[right])
                {
                    // Not a transposition at all — swapping two identical characters produces the
                    // same string. Asserted rather than skipped, so every pair is accounted for and
                    // the count below stays exact.
                    swapped.Should().Be(number);
                    IdGenerator.IsValidTransactionNumber(swapped).Should().BeTrue();
                    continue;
                }

                IdGenerator.IsValidTransactionNumber(swapped).Should().BeFalse(
                    "{0} transposes two adjacent characters of {1} and must not validate", swapped, number);
            }
        }

        exercised.Should().Be(expected);

        // `expected` and the generator share the same predicate, so a suffix/check pair that never
        // fired would leave both sides agreeing on a vacuous count. This pins it independently:
        // 32 of the 37 residues land in the encoding alphabet, so all 25 draws missing is a
        // (5/37)^25 event — around 1e-22, which is to say it does not happen.
        suffixCheckPairsRun.Should().BePositive(
            "the suffix/check transposition is the case with its own proof and must actually run");
    }

    [Fact]
    public void ATypoInTheCheckSymbolItselfIsRejected()
    {
        // The complement of the two tests above: they mutate the value and keep the symbol, this
        // keeps the value and mutates the symbol. Both directions are how a misread receipt arrives.
        var number = IdGenerator.GenerateTransactionNumber();
        var exercised = 0;

        foreach (var replacement in CheckSymbols)
        {
            if (replacement == number[^1])
            {
                continue;
            }

            var mutant = string.Concat(number.AsSpan(0, number.Length - 1), replacement.ToString());
            mutant.Should().MatchRegex(WellFormed);
            IdGenerator.IsValidTransactionNumber(mutant).Should().BeFalse();
            exercised++;
        }

        exercised.Should().Be(36, "the check alphabet is 32 encoding symbols plus *~$=U, minus the real one");
    }

    [Fact]
    public void ANumberRetypedWithCrockfordConfusablesStillValidates()
    {
        /*
          This is the reason the alphabet drops I, L, O and U in the first place. A person reading a
          number off a receipt writes O for 0 and I or l for 1; because those letters can never be
          part of the value, decoding can reinterpret them instead of rejecting them — which turns
          the commonest transcription mistake into no mistake at all rather than into a failed lookup.

          Drawn until the payload holds both a 0 and a 1 so all three confusables are actually
          exercised; a number containing neither would pass this test without testing anything.
        */
        var number = Enumerable.Range(0, 500)
            .Select(_ => IdGenerator.GenerateTransactionNumber())
            .First(n => Payload(n).Contains('0') && Payload(n).Contains('1'));

        IdGenerator.IsValidTransactionNumber(number.Replace('0', 'O').Replace('1', 'I').ToLowerInvariant())
            .Should().BeTrue("O reads as 0, I reads as 1, and case is folded");
        IdGenerator.IsValidTransactionNumber(number.Replace('1', 'L'))
            .Should().BeTrue("L reads as 1 as well");
    }

    [Fact]
    public void APreMigrationNumberDoesNotValidate()
    {
        /*
          Stated as a test because it is the one uncomfortable consequence of the format change, and
          a comment claiming it would be easy to falsify by accident. Rows written before
          WidenTransactionNumberForCheckSymbol carry the old 20-character number and have no check
          symbol, so this returns false for them.

          That is safe only because nothing validates stored numbers: no endpoint takes a transaction
          number as input, and EnforceTransactionImmutability forbids renumbering a saved row. If a
          lookup-by-number endpoint is ever added, it must not gate on this without a format check
          for the legacy shape first.
        */
        IdGenerator.IsValidTransactionNumber("TXN-20260805-K3M9PZ2").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("TXN-20260805-K3M9PZ2QW4")] // 23 characters: one short
    [InlineData("TXN-20260805-K3M9PZ2QW4XX")] // 25 characters: one long
    [InlineData("XXX-20260805-K3M9PZ2QW4X")] // right length, wrong prefix
    [InlineData("TXN-2026080A-K3M9PZ2QW4X")] // a letter where the date must be digits
    [InlineData("TXN-20260805-K3M9PZ2QW4-")] // a hyphen is in neither alphabet
    public void MalformedInputIsRejected(string? candidate) =>
        IdGenerator.IsValidTransactionNumber(candidate).Should().BeFalse();

    /// <summary>
    /// Adjacent index pairs inside the date, inside the suffix, and — when it can be tested for the
    /// right reason — the pair that straddles the suffix and the check symbol.
    ///
    /// <para>
    /// Not the pair across the hyphen at 12: that swap moves a letter into the date, so the format
    /// gate turns it away and the test would prove nothing about the residue.
    /// </para>
    /// <para>
    /// The suffix/check pair (22, 23) is the interesting one, because the mod-37 transposition
    /// argument does NOT cover it — that argument is about two characters inside the payload, and
    /// the check symbol is the residue, not payload. Worked through: with payload <c>P</c> ending in
    /// <c>a</c>, residue <c>R</c> and symbol <c>c</c>, the swap gives a payload whose residue is
    /// <c>R + val(c) − val(a)</c>, and it validates only when <c>2(R − val(a)) ≡ 0 (mod 37)</c>.
    /// 37 is odd, so 2 is invertible and that means <c>R = val(a)</c> — precisely the case where
    /// <c>c</c> and <c>a</c> are the SAME character and the swap changed nothing. Every real
    /// suffix/check transposition is therefore rejected, for a different reason than the others.
    /// </para>
    /// <para>
    /// Included only when the check symbol is an encoding symbol. When it is one of <c>*~$=U</c> the
    /// swap puts a character in the suffix that the alphabet forbids, and the rejection would come
    /// from the format gate — true, but not the thing under test.
    /// </para>
    /// </summary>
    private static IEnumerable<(int Left, int Right)> AdjacentPayloadPairs(string number)
    {
        for (var i = 4; i < 11; i++)
        {
            yield return (i, i + 1);
        }

        for (var i = 13; i < 22; i++)
        {
            yield return (i, i + 1);
        }

        if (Base32.Contains(number[^1]))
        {
            yield return (22, 23);
        }
    }

    private static string Swap(string value, int left, int right)
    {
        var chars = value.ToCharArray();
        (chars[left], chars[right]) = (chars[right], chars[left]);
        return new string(chars);
    }

    #endregion
}
