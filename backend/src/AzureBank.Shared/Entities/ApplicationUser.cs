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

    /// <summary>
    /// How many times every session of this user has been signed out at once (ADR-0057 §5.3).
    /// Sign-in, registration and re-authentication hand the value to the BFF, which keeps it on the
    /// session; every per-user sign-out — <c>POST /api/auth/logout</c>, or a runbook's SQL — adds 1
    /// in the same transaction as its grant revoke. The BFF then refuses every session whose value
    /// is below the one it last read (<c>POST /api/auth/session-stamps</c>), within one 15-second
    /// poll.
    /// </summary>
    /// <remarks>
    /// Not Identity's <c>SecurityStamp</c>, which has been in this table since InitialCreate and keeps
    /// its Identity meaning: Identity rewrites that one on a password or two-factor change, and those
    /// are not sign-outs. A counter, not a random value, so "below" means "older" and a read that
    /// arrives late can never lower what the BFF knows. Every raise also rotates
    /// <c>ConcurrencyStamp</c>: Identity's <c>UpdateAsync</c> writes back every column of a user it
    /// loaded before the raise, this one included, and that rotation makes such a write fail instead.
    /// </remarks>
    public int SessionStamp { get; set; }

    /// <summary>
    /// The demo copy this user belongs to. Null for every user outside the demo.
    /// </summary>
    /// <remarks>
    /// The owner and the two contacts of a copy carry the same id, a foreign key to
    /// <see cref="DemoCopy"/>. This column, and no name pattern or age, is what says which users
    /// are a copy's.
    /// </remarks>
    public Guid? DemoCopyId { get; set; }

    [Required]
    public required string FirstName { get; set; }
    [Required]
    public required string LastName { get; set; }

    [NotMapped]
    public string FullName => $"{FirstName} {LastName}";

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