using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.Entities;
using Riok.Mapperly.Abstractions;

namespace AzureBank.Api.Mappers;

/// <summary>
/// Mapperly-based mapper for Account entity to DTO conversions.
/// Source generator - no runtime reflection overhead.
/// </summary>
/// <remarks>
/// Uses RequiredMappingStrategy.Target: only validates DTO properties are filled.
/// Intentionally unmapped entity properties (by design):
/// - UserId, RowVersion: Internal FK / concurrency token
/// - User, Transactions: Navigation properties
/// - UpdatedAt, IsDeleted, DeletedAt: Audit / soft-delete fields
/// </remarks>
[Mapper(RequiredMappingStrategy = RequiredMappingStrategy.Target)]
public partial class AccountMapper
{
    /// <summary>
    /// Maps Account entity to full AccountResponse DTO.
    /// AccountNumber is masked for privacy; all other strings map verbatim.
    /// </summary>
    [MapProperty(nameof(Account.AccountNumber), nameof(AccountResponse.AccountNumber),
        Use = nameof(MaskAccountNumber))]
    public partial AccountResponse ToResponse(Account entity);

    /// <summary>
    /// Maps list of Account entities to list of AccountResponse DTOs.
    /// </summary>
    public partial List<AccountResponse> ToResponseList(List<Account> entities);

    // ToSummary removed - MaskedAccountNumber requires custom mapping
    // Use ToSummaryWithMask or ToSummaryWithMaskList instead

    /// <summary>
    /// Maps Account entity to AccountSummaryResponse with masked account number.
    /// Used for list views where full account number should not be displayed.
    /// </summary>
    public AccountSummaryResponse ToSummaryWithMask(Account entity)
    {
        return new AccountSummaryResponse
        {
            Id = entity.Id,
            MaskedAccountNumber = MaskAccountNumber(entity.AccountNumber),
            Name = entity.Name,
            Type = entity.Type,
            Balance = entity.Balance
        };
    }

    /// <summary>
    /// Maps list of Account entities to list of AccountSummaryResponse with masked numbers.
    /// </summary>
    public List<AccountSummaryResponse> ToSummaryWithMaskList(List<Account> entities)
    {
        return entities.Select(ToSummaryWithMask).ToList();
    }

    /// <summary>
    /// Creates a BalanceResponse from account data.
    /// </summary>
    public BalanceResponse ToBalanceResponse(Account entity, DateTime asOf, bool isHistorical = false)
    {
        return new BalanceResponse
        {
            AccountId = entity.Id,
            Balance = entity.Balance,
            Currency = "EUR",
            AsOf = asOf,
            IsHistorical = isHistorical
        };
    }

    /// <summary>
    /// Creates a BalanceResponse for historical balance query.
    /// </summary>
    public BalanceResponse ToHistoricalBalanceResponse(Guid accountId, decimal balance, DateTime asOf)
    {
        return new BalanceResponse
        {
            AccountId = accountId,
            Balance = balance,
            Currency = "EUR",
            AsOf = asOf,
            IsHistorical = true
        };
    }

    /// <summary>
    /// Masks account number for privacy.
    /// AB-1234-5678-90 -> AB-****-****-90
    /// </summary>
    /// <remarks>
    /// [UserMapping(Default = false)]: without it, Mapperly silently adopts this
    /// method as THE default string-to-string conversion, masking EVERY string
    /// property in generated mappings (Account.Name came back as "Tes****-****-nt").
    /// It is now applied only where referenced explicitly via MapProperty(Use=...).
    /// </remarks>
    [UserMapping(Default = false)]
    private static string MaskAccountNumber(string accountNumber)
    {
        if (string.IsNullOrEmpty(accountNumber) || accountNumber.Length < 14)
            return accountNumber;

        return $"{accountNumber[..3]}****-****-{accountNumber[^2..]}";
    }
}
