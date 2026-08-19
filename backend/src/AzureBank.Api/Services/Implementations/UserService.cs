using AzureBank.Api.Mappers;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Enums;
using AzureBank.Shared.DTOs.User;
using AzureBank.Shared.Exceptions;
using AzureBank.Shared.Utilities;
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
    private readonly IAuditService _audit;

    public UserService(AzureBankDbContext context, ILogger<UserService> logger, IAuditService audit)
    {
        _context = context;
        _mapper = new UserMapper();
        _logger = logger;
        _audit = audit;
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

        /*
          BEFORE the save, so the rename and its evidence are ONE unit of work (ADR-0044 D1). The
          first version recorded AFTER the save and then saved a SECOND time, which meant the rename
          could commit while the row that proves it failed to — the exact split D1 exists to prevent,
          and invisible to a test that only checks Record was called.

          NEITHER HANDLE goes in the row, and that is the D5 rule rather than an oversight: a handle
          is user-chosen and public, which makes it exactly the descriptive personal data this table
          must not hold, on a store designed never to be purged. The subject is the USER, and both
          handles stay in the log line below, which does expire.
        */
        _audit.Record(
            SecurityEvents.AzureTagRenamed, AuditOutcome.Succeeded,
            actorUserId: userId, subjectType: "User", subjectId: userId);

        try
        {
            // A plain column update — UserName is the immutable id, so no Identity username
            // change is involved (ADR-0015). The audit row added above rides THIS save.
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ConcurrencyRetry.IsAzureTagCollision(ex))
        {
            // Lost the AzureTag unique-index race to a concurrent claim of the same handle.
            // Narrowed by INDEX NAME rather than by error number, and that is load-bearing now that
            // the audit row shares this save: IX_AuditEvents_Sequence raises the same 2601/2627, so
            // matching the number alone would report a hash-chain collision as "handle taken".
            // Every other DbUpdateException (deadlock, timeout, connectivity, an unrelated
            // constraint) still propagates rather than being misreported.
            throw new ConflictException("That handle is already taken.", ErrorCodes.AzureTagTaken);
        }

        /*
          Sanitized for the LOG only; `normalized` is still returned unchanged.

          Both values are AzureTags and therefore already pattern-constrained (anchored
          ^[a-z][a-z0-9_]{2,19}$), which is the argument this alert was dismissed on. It reopened
          the moment the line moved, and it will keep reopening: the barrier is what ends that, and
          it does not rely on a validator in a different file staying in force on every path.
          `previous` is the older handle read back from the row — user-authored once, so it gets the
          same treatment as the new one rather than being trusted for having been stored.
        */
        _logger.LogInformation(
            "SecurityEvent {SecurityEvent}: user {UserId} renamed their handle from {PreviousAzureTag} to {AzureTag}",
            SecurityEvents.AzureTagRenamed,
            userId,
            LogSanitizer.Sanitize(previous),
            LogSanitizer.Sanitize(normalized));

        return normalized;
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
