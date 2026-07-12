using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Seeds sample accounts for test users.
/// Creates accounts with predefined balances for testing.
/// </summary>
public class AccountSeeder : ISeeder
{
    private readonly AzureBankDbContext _context;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<AccountSeeder> _logger;

    public string Name => "AccountSeeder";
    public int Order => 3; // After UserSeeder

    public AccountSeeder(
        AzureBankDbContext context,
        UserManager<ApplicationUser> userManager,
        ILogger<AccountSeeder> logger)
    {
        _context = context;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent: skip if accounts already exist
        if (await _context.Accounts.AnyAsync(cancellationToken))
        {
            _logger.LogInformation("Accounts already exist. Skipping account seeding.");
            return;
        }

        // Get seeded users
        var users = await UserSeeder.GetSeededUsersAsync(_userManager, cancellationToken);
        if (users.Count == 0)
        {
            _logger.LogWarning("No users found. Cannot seed accounts.");
            return;
        }

        var accounts = CreateAccounts(users);

        await _context.Accounts.AddRangeAsync(accounts, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Seeded {Count} accounts", accounts.Count);
    }

    /// <summary>
    /// Creates sample accounts for seeded users.
    /// </summary>
    private List<Account> CreateAccounts(List<ApplicationUser> users)
    {
        var accounts = new List<Account>();

        var john = users.FirstOrDefault(u => u.AzureTag == "johnsmith");
        var jane = users.FirstOrDefault(u => u.AzureTag == "janesmith");
        var mike = users.FirstOrDefault(u => u.AzureTag == "mikebrown");
        var admin = users.FirstOrDefault(u => u.AzureTag == "admin");

        // John's accounts (2 accounts)
        if (john != null)
        {
            accounts.Add(new Account
            {
                UserId = john.Id,
                AccountNumber = "AB-1234-5678-90",
                Name = "Main Savings",
                Type = AccountType.Savings,
                Balance = 12450.00m,
                IsPrimary = true,
                User = john
            });

            accounts.Add(new Account
            {
                UserId = john.Id,
                AccountNumber = "AB-1234-5678-91",
                Name = "Checking",
                Type = AccountType.Checking,
                Balance = 2300.00m,
                IsPrimary = false,
                User = john
            });

            _logger.LogDebug("Created 2 accounts for John");
        }

        // Jane's account
        if (jane != null)
        {
            accounts.Add(new Account
            {
                UserId = jane.Id,
                AccountNumber = "AB-2345-6789-01",
                Name = "Personal Savings",
                Type = AccountType.Savings,
                Balance = 8500.00m,
                IsPrimary = true,
                User = jane
            });

            _logger.LogDebug("Created 1 account for Jane");
        }

        // Mike's account
        if (mike != null)
        {
            accounts.Add(new Account
            {
                UserId = mike.Id,
                AccountNumber = "AB-3456-7890-12",
                Name = "Investment Account",
                Type = AccountType.Investment,
                Balance = 25000.00m,
                IsPrimary = true,
                User = mike
            });

            _logger.LogDebug("Created 1 account for Mike");
        }

        // Admin's account
        if (admin != null)
        {
            accounts.Add(new Account
            {
                UserId = admin.Id,
                AccountNumber = "AB-0000-0000-01",
                Name = "Admin Account",
                Type = AccountType.Checking,
                Balance = 50000.00m,
                IsPrimary = true,
                User = admin
            });

            _logger.LogDebug("Created 1 account for Admin");
        }

        return accounts;
    }
}
