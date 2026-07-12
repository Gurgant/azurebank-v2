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

        // Assert - Should have very high uniqueness (allowing small collision chance)
        numbers.Should().HaveCountGreaterThan((int)(count * 0.99));
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
        txnNumber.Should().MatchRegex(@"^TXN-\d{8}-\d{6}$");
    }

    [Fact]
    public void GenerateTransactionNumber_HasCorrectLength()
    {
        // Act
        var txnNumber = IdGenerator.GenerateTransactionNumber();

        // Assert - "TXN-20260114-123456" = 19 characters
        txnNumber.Should().HaveLength(19);
    }

    [Fact]
    public void GenerateTransactionNumber_GeneratesUniqueNumbers()
    {
        // Arrange
        const int count = 100;

        // Act
        var numbers = Enumerable.Range(0, count)
            .Select(_ => IdGenerator.GenerateTransactionNumber())
            .ToHashSet();

        // Assert - Should have high uniqueness
        numbers.Should().HaveCountGreaterThan((int)(count * 0.95));
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
