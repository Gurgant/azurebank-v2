using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Options;
using AzureBank.Shared.Services.Interfaces;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Seeds test users with Identity integration.
/// Creates: johnsmith, janesmith, mikebrown (User role), admin (Admin role).
/// </summary>
public class UserSeeder : ISeeder
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPasswordHasher _pinHasher;
    private readonly SeedDataOptions _seedOptions;
    private readonly ILogger<UserSeeder> _logger;

    public string Name => "UserSeeder";
    public int Order => 2; // After RoleSeeder

    public UserSeeder(
        UserManager<ApplicationUser> userManager,
        IPasswordHasher pinHasher,
        IOptions<SeedDataOptions> seedOptions,
        ILogger<UserSeeder> logger)
    {
        _userManager = userManager;
        _pinHasher = pinHasher;
        _seedOptions = seedOptions.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent: skip if users already exist
        if (await _userManager.Users.AnyAsync(cancellationToken))
        {
            _logger.LogInformation("Users already exist. Skipping user seeding.");
            return;
        }

        var pinHash = _pinHasher.HashPin(_seedOptions.DefaultPin);

        // Define seed users: (azureTag, email, firstName, lastName, role)
        var seedUsers = new[]
        {
            ("johnsmith", "john@example.com", "John", "Smith", Roles.User),
            ("janesmith", "jane@example.com", "Jane", "Smith", Roles.User),
            ("mikebrown", "mike@example.com", "Mike", "Brown", Roles.User),
            ("admin", _seedOptions.AdminEmail, "Admin", "User", Roles.Admin)
        };

        foreach (var (azureTag, email, firstName, lastName, role) in seedUsers)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // UserName is the immutable user id, not the AzureTag (ADR-0015).
            var userId = Guid.CreateVersion7();
            var user = new ApplicationUser
            {
                // Identity properties
                Id = userId,
                UserName = userId.ToString(),
                Email = email,
                EmailConfirmed = true,

                // Custom properties
                AzureTag = azureTag,
                PinHash = pinHash,
                FirstName = firstName,
                LastName = lastName
            };

            // UserManager handles password hashing, normalization, security stamps
            var result = await _userManager.CreateAsync(user, _seedOptions.DefaultPassword);

            if (result.Succeeded)
            {
                await _userManager.AddToRoleAsync(user, role);
                _logger.LogInformation("Created user: {Email} with role {Role}", email, role);
            }
            else
            {
                _logger.LogError("Failed to create user {Email}: {Errors}",
                    email, string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
    }

    /// <summary>
    /// Gets the list of seeded users (for AccountSeeder).
    /// </summary>
    public static async Task<List<ApplicationUser>> GetSeededUsersAsync(
        UserManager<ApplicationUser> userManager,
        CancellationToken cancellationToken = default)
    {
        var azureTags = new[] { "johnsmith", "janesmith", "mikebrown", "admin" };
        var users = new List<ApplicationUser>();

        foreach (var tag in azureTags)
        {
            var user = await userManager.Users
                .FirstOrDefaultAsync(u => u.AzureTag == tag, cancellationToken);

            if (user != null)
                users.Add(user);
        }

        return users;
    }
}
