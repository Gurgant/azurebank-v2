using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AzureBank.Api.Services.Implementations;

/// <summary>
/// User service handling profile and recipient lookup operations.
/// </summary>
public class UserService : IUserService
{
    private readonly AzureBankDbContext _context;
    private readonly UserMapper _mapper;
    private readonly ILogger<UserService> _logger;

    public UserService(AzureBankDbContext context, ILogger<UserService> logger)
    {
        _context = context;
        _mapper = new UserMapper();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<UserResponse> GetUserByIdAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);

        if (user == null)
        {
            throw new NotFoundException("User", userId);
        }

        return _mapper.ToResponse(user);
    }

    /// <inheritdoc />
    public async Task<RecipientLookupResponse> GetUserByAzureTagAsync(string azureTag, Guid currentUserId)
    {
        // AzureTags are stored lower-cased; normalise the same way (invariant, not the
        // current culture — a Turkish-I difference would silently mismatch).
        var normalizedTag = azureTag.ToLowerInvariant();

        // EXACT match only — no substring/prefix search (ADR-0014). Project to just the
        // fields the response needs so the query never materialises the full ApplicationUser
        // (PasswordHash / PinHash / SecurityStamp) into memory.
        var match = await _context.Users
            .Where(u => u.AzureTag == normalizedTag)
            .Select(u => new { u.Id, u.FirstName, u.LastName })
            .FirstOrDefaultAsync();

        // Looking up yourself is not a valid transfer recipient — report not-found without
        // echoing a name (the transfer endpoint blocks self-transfer separately).
        if (match is not null && match.Id == currentUserId)
        {
            return new RecipientLookupResponse { AzureTag = azureTag, DisplayName = string.Empty, Exists = false };
        }

        return new RecipientLookupResponse
        {
            AzureTag = azureTag,
            DisplayName = match is not null ? MaskDisplayName(match.FirstName, match.LastName) : string.Empty,
            Exists = match is not null
        };
    }

    /// <inheritdoc />
    public async Task<string> RenameAzureTagAsync(Guid userId, string newAzureTag)
    {
        var normalized = newAzureTag.ToLowerInvariant();

        // Reject a handle already held by someone else. Unlike registration, revealing
        // "taken" here is fine — the exact-match lookup already confirms handle existence.
        var takenByOther = await _context.Users.AnyAsync(u => u.AzureTag == normalized && u.Id != userId);
        if (takenByOther)
        {
            throw new ConflictException("That handle is already taken.", ErrorCodes.AzureTagTaken);
        }

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId)
            ?? throw new NotFoundException("User", userId);

        if (user.AzureTag == normalized)
        {
            return normalized; // no-op: already the caller's handle
        }

        var previous = user.AzureTag;
        user.AzureTag = normalized;
        user.UpdatedAt = DateTime.UtcNow;

        try
        {
            // A plain column update — UserName is the immutable id, so no Identity username
            // change is involved (ADR-0015).
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsAzureTagUniqueViolation(ex))
        {
            // Lost the AzureTag unique-index race to a concurrent claim of the same handle.
            // Only the unique-index violation maps to 409 — every other DbUpdateException
            // (deadlock, timeout, connectivity, an unrelated constraint) must propagate,
            // not be misreported as "handle taken".
            throw new ConflictException("That handle is already taken.", ErrorCodes.AzureTagTaken);
        }

        _logger.LogInformation(
            "SecurityEvent {SecurityEvent}: user {UserId} renamed their handle from {PreviousAzureTag} to {AzureTag}",
            "AzureTagRenamed", userId, previous, normalized);

        return normalized;
    }

    // SQL Server unique-violation error numbers (2627 = PK/UNIQUE constraint, 2601 = duplicate
    // row in a unique index) — mirrors IdempotencyService's narrowing so a concurrent AzureTag
    // claim maps to 409 while every other write error (deadlock, timeout, …) propagates.
    private const int SqlPrimaryKeyViolation = 2627;
    private const int SqlUniqueIndexViolation = 2601;

    private static bool IsAzureTagUniqueViolation(DbUpdateException ex)
    {
        // The SqlException can sit several levels down the InnerException chain.
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql
                && (sql.Number is SqlPrimaryKeyViolation or SqlUniqueIndexViolation
                    || sql.Errors.Cast<SqlError>().Any(
                        e => e.Number is SqlPrimaryKeyViolation or SqlUniqueIndexViolation)))
            {
                return true;
            }
        }

        return false;
    }

    // "Vladislav A." — enough to confirm the right payee, not the full surname (ADR-0014).
    // Trim for display robustness only: the name charset permits edge spaces and registration
    // persists names untrimmed, so a surname like " Smith" would otherwise mask to "John  ."
    // (This is presentation normalisation, not input validation.)
    private static string MaskDisplayName(string firstName, string? lastName)
    {
        var first = firstName.Trim();
        return string.IsNullOrWhiteSpace(lastName) ? first : $"{first} {lastName.Trim()[0]}.";
    }
}
