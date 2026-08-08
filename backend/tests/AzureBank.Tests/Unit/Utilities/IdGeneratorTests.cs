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

          The assertion is still tightened to zero duplicates: in 1,000 draws from 7.29e9 the
          expected count is 7e-5, so tolerating ten was tolerating a generator four orders of
          magnitude worse than this one.
        */
        // Zero is safe HERE: 1,000 draws from 7.29e9 expect 7e-5 duplicates, so a correct
        // generator fails about once in 14,000 runs — three orders of magnitude rarer than the
        // transaction-number sample above, which is why that one allows a duplicate and this
        // does not.
        numbers.Should().HaveCount(count, "AccountNumber is unique-indexed too");
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

        // Assert - Format: TXN-YYYYMMDD-NNNNNN
        // Crockford base32: digits and letters, with I, L, O and U excluded so nothing in a
        // number a human retypes can be confused with 1 or 0.
        txnNumber.Should().MatchRegex(@"^TXN-\d{8}-[0-9A-HJKMNP-TV-Z]{7}$");
    }

    [Fact]
    public void GenerateTransactionNumber_HasCorrectLength()
    {
        // Act
        var txnNumber = IdGenerator.GenerateTransactionNumber();

        // Assert - "TXN-20260114-123456" = 19 characters
        // 20 = ValidationRules.TransactionNumberLength, exactly filling the existing column: the
        // widening from 6 to 7 suffix characters needed no migration.
        txnNumber.Should().HaveLength(20);
    }

    [Fact]
    public void GenerateTransactionNumber_GeneratesUniqueNumbers()
    {
        /*
          20,000 and not 100, because the sample size is what decides whether this test can SEE the
          entropy at all. At the old six digits (900,000/day) a hundred draws collide only ~0.55% of
          the time, so even a zero-tolerance assertion would have passed on the broken generator
          more than 99 times out of 100 — tightening the tolerance alone fixes nothing.

          At 20,000 draws the old generator is expected to produce ~222 duplicates and effectively
          never comes back clean, while the widened one (32^7 per day) expects 0.0058 — so this
          number is the smallest that makes the test a real detector rather than a formality.
          Verified by reverting the generator: this test goes red.
        */
        const int count = 20_000;

        // Act
        var numbers = Enumerable.Range(0, count)
            .Select(_ => IdGenerator.GenerateTransactionNumber())
            .ToHashSet();

        /*
          INTOLERANT on purpose. This assertion used to read
          `HaveCountGreaterThan((int)(count * 0.95))` — "allowing small collision chance", per its
          own comment — which made it structurally incapable of reporting the bug it was named for.
          TransactionNumber carries a UNIQUE index, so a collision is not a small chance, it is a
          failed INSERT and a 500 on a money endpoint.

          At the old six digits (900,000 values per UTC day) a duplicate inside 100 draws had a
          probability near 0.55%, so a test tolerating five was passing on a generator that reaches
          even odds of collision at ~1,117 transactions per day. With the widened suffix the
          expected number of duplicates in 100 draws is about 1.4e-7; a single one here is a real
          regression, not noise.
        */
        /*
          At most ONE duplicate, not zero — and the difference matters, because a zero-duplicate
          assertion here would be FLAKY AGAINST A CORRECT GENERATOR. 20,000 draws from 32^7 expect
          0.0058 duplicates, so P(at least one) is about 0.58%: roughly one CI run in 172 would go
          red with nothing wrong. Caught in review before it could start costing people evenings.

          One is the right threshold: P(two or more) is about 1.7e-5, once in 59,000 runs, while the
          old six-digit generator expects ~222 duplicates and comes back with ~19,778 distinct. The
          gap between 19,999 and 19,778 is enormous, so this stays a real detector.

          Read it as an ENTROPY regression test. Uniqueness itself is not a property of the
          generator and is not asserted here — it belongs to the database, and
          TransactionNumberUniquenessSqlServerTests proves the index enforces it.
        */
        numbers.Should().HaveCountGreaterThan(
            count - 2,
            "the widened suffix must not produce more than one duplicate in 20,000 draws");
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
}
