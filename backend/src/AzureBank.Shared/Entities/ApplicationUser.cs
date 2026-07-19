using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AzureBank.Shared.Entities;

public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// Public handle for transfers (e.g., "@johnsmith"), stored lowercase and unique.
    /// Renameable (ADR-0015) and NOT a login credential: Identity's UserName is the
    /// immutable user id, and login is by email. This is a plain profile column.
    /// </summary>
    [Required]
    public required string AzureTag { get; set; }
    /// <summary>
    /// 6-digit PIN hash for step-up authentication (transfers, sensitive operations)
    /// </summary>
    public string? PinHash { get; set; }

    /// <summary>
    /// Consecutive wrong PIN attempts since the last success. Reset to 0 on a
    /// correct PIN, and also when the threshold is crossed and the PIN is locked
    /// (the lockout window then becomes authoritative). Kept separate from
    /// Identity's AccessFailedCount (password) so PIN and password lockouts never interfere.
    /// </summary>
    public int PinAccessFailedCount { get; set; }

    /// <summary>
    /// When set and in the future, the PIN is locked until this instant (too
    /// many wrong attempts). Null when the PIN is not locked.
    /// </summary>
    public DateTimeOffset? PinLockoutEnd { get; set; }

    [Required]
    public required string FirstName { get; set; }
    [Required]
    public required string LastName { get; set; }

    [NotMapped]
    public string FullName => $"{FirstName} {LastName}";
    // TODO: ProfilePictureUrl?

    /// <summary>
    /// Timestamp when the user was created. Set automatically by DbContext.UpdateTimestamps().
    /// </summary>
    public DateTime CreatedAt { get; set; } 

    public DateTime? UpdatedAt { get; set; }

    public ICollection<Account> Accounts { get; set; } = [];
}

// Note: IdentityUser<Guid> provides these properties automatically:
// - Id (Guid)
// - UserName (string)
// - NormalizedUserName (string)
// - Email (string)
// - NormalizedEmail (string)
// - EmailConfirmed (bool)
// - PasswordHash (string) - handled by Identity's password hasher
// - SecurityStamp (string)
// - ConcurrencyStamp (string)
// - PhoneNumber (string)
// - PhoneNumberConfirmed (bool)
// - TwoFactorEnabled (bool)
// - LockoutEnd (DateTimeOffset?)
// - LockoutEnabled (bool)
// - AccessFailedCount (int)