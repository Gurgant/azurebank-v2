# AzureBank Seeder Tool - Implementation Plan

> **Document Version**: 1.0
> **Created**: 2026-01-20
> **Status**: Planned (Pending Implementation)
> **Related**: Clean Architecture, Database Seeding, Enterprise Patterns

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Current State Audit](#2-current-state-audit)
3. [Target Architecture](#3-target-architecture)
4. [Implementation Plan](#4-implementation-plan)
5. [Testing & Validation](#5-testing--validation)
6. [References](#6-references)

---

## 1. Executive Summary

### 1.1 Problem Statement

The current architecture has a **hidden circular dependency**:
- `IPasswordHasher` interface is in `AzureBank.Shared` (correct)
- `PasswordHasher` implementation is in `AzureBank.Api`
- `DatabaseSeeder` in `AzureBank.Infrastructure` needs `IPasswordHasher`
- This creates: Infrastructure → (needs) → Api implementation (violation!)

### 1.2 Solution

Create a **separate console tool** (`AzureBank.Seeder`) that:
- Lives outside the main architecture
- Can reference ALL projects freely
- Handles all seeding operations
- Removes seeding responsibility from Infrastructure

### 1.3 Benefits

| Benefit | Description |
|---------|-------------|
| ✅ Clean Architecture | No hidden circular dependencies |
| ✅ Portable | Run seeding independently of Api |
| ✅ CI/CD Ready | Integrate into deployment pipelines |
| ✅ Testable | Easy to test seeders in isolation |
| ✅ Flexible | Add new seeders without touching Api |
| ✅ Enterprise Pattern | Industry-standard approach |

---

## 2. Current State Audit

### 2.1 Architecture Diagram (Current - PROBLEMATIC)

```
┌─────────────────────────────────────────────────────────────────────┐
│                        CURRENT ARCHITECTURE                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────────┐                                               │
│  │  AzureBank.Api   │                                               │
│  │                  │                                               │
│  │  • PasswordHasher│◄─────── Implementation lives here             │
│  │  • Program.cs    │         but Infrastructure needs it!          │
│  │  • Controllers   │                                               │
│  └────────┬─────────┘                                               │
│           │ references                                               │
│           ▼                                                          │
│  ┌──────────────────┐                                               │
│  │  Infrastructure  │                                               │
│  │                  │                                               │
│  │  • DatabaseSeeder│◄─────── Needs IPasswordHasher                 │
│  │  • DbContext     │         Creates circular dependency!          │
│  │  • Migrations    │                                               │
│  └────────┬─────────┘                                               │
│           │ references                                               │
│           ▼                                                          │
│  ┌──────────────────┐                                               │
│  │  AzureBank.Shared│                                               │
│  │                  │                                               │
│  │  • IPasswordHasher◄─────── Interface is here (correct)          │
│  │  • Entities      │                                               │
│  │  • DTOs          │                                               │
│  └──────────────────┘                                               │
│                                                                      │
│  ❌ PROBLEM: Infrastructure depends on Api's implementation         │
│             via DI at runtime (hidden circular dependency)           │
└─────────────────────────────────────────────────────────────────────┘
```

### 2.2 Files Inventory

| File | Location | Role | Action Needed |
|------|----------|------|---------------|
| `IPasswordHasher.cs` | `Shared/Services/Interfaces/` | Interface | Keep ✅ |
| `PasswordHasher.cs` | `Api/Services/Implementations/` | Implementation | Move to Shared |
| `DatabaseSeeder.cs` | `Infrastructure/Data/Seed/` | Seeding | Move to Seeder Tool |
| `SeedDataOptions.cs` | `Shared/Options/` | Configuration | Keep ✅ |
| `ServiceCollectionExtensions.cs` | `Infrastructure/Extensions/` | DI | Remove seeder registration |
| `WebApplicationExtensions.cs` | `Api/Extensions/` | Seeding trigger | Remove seeding method |

### 2.3 DatabaseSeeder Dependencies

```csharp
public DatabaseSeeder(
    AzureBankDbContext context,                    // From Infrastructure
    UserManager<ApplicationUser> userManager,     // From Identity
    RoleManager<IdentityRole<Guid>> roleManager,  // From Identity
    IPasswordHasher pinHasher,                    // From Shared (interface)
    IOptions<SeedDataOptions> seedOptions,        // From Shared
    ILogger<DatabaseSeeder> logger)               // From Microsoft.Extensions
```

### 2.4 Seeded Data Summary

#### Roles
- User
- Admin

#### Users (4 total)
| AzureTag | Email | Role | Password | PIN |
|----------|-------|------|----------|-----|
| johnsmith | john@example.com | User | Test123! | 123456 |
| janesmith | jane@example.com | User | Test123! | 123456 |
| mikebrown | mike@example.com | User | Test123! | 123456 |
| admin | admin@azurebank.dev | Admin | Test123! | 123456 |

#### Accounts (5 total)
- John: Main Savings ($12,450), Checking ($2,300)
- Jane: Personal Savings ($8,500)
- Mike: Investment Account ($25,000)
- Admin: Admin Account ($50,000)

#### Transactions (4 total on John's savings)
- Salary deposit, ATM withdrawal, Online purchase, Refund

---

## 3. Target Architecture

### 3.1 Architecture Diagram (Target - CLEAN)

```
┌─────────────────────────────────────────────────────────────────────┐
│                        TARGET ARCHITECTURE                           │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌──────────────────┐     ┌──────────────────┐                      │
│  │  AzureBank.Api   │     │ AzureBank.Seeder │◄── NEW TOOL          │
│  │                  │     │  (Console App)   │                      │
│  │  • Controllers   │     │                  │                      │
│  │  • Services      │     │  • CLI Commands  │                      │
│  │  • Middleware    │     │  • Seeders       │                      │
│  └────────┬─────────┘     └────────┬─────────┘                      │
│           │                        │                                 │
│           │ references             │ references ALL                  │
│           ▼                        ▼                                 │
│  ┌──────────────────┐     ┌──────────────────┐                      │
│  │  Infrastructure  │◄────│  Can reference   │                      │
│  │                  │     │  Api, Infra,     │                      │
│  │  • DbContext     │     │  Shared freely!  │                      │
│  │  • Migrations    │     └──────────────────┘                      │
│  │  • NO SEEDING    │◄─────── Seeding removed!                      │
│  └────────┬─────────┘                                               │
│           │                                                          │
│           ▼                                                          │
│  ┌──────────────────┐                                               │
│  │  AzureBank.Shared│                                               │
│  │                  │                                               │
│  │  • IPasswordHasher                                               │
│  │  • PasswordHasher │◄─────── Implementation moved here            │
│  │  • Entities      │                                               │
│  │  • SeedDataOptions                                               │
│  └──────────────────┘                                               │
│                                                                      │
│  ✅ SOLUTION: Seeder tool is external, can reference everything     │
│              PasswordHasher in Shared for all to use                 │
│              No circular dependencies, clean architecture            │
└─────────────────────────────────────────────────────────────────────┘
```

### 3.2 Project Structure (Target)

```
backend/
├── src/
│   ├── AzureBank.Api/
│   │   ├── Controllers/
│   │   ├── Services/
│   │   │   └── Implementations/
│   │   │       └── (PasswordHasher.cs REMOVED)
│   │   └── ...
│   ├── AzureBank.Infrastructure/
│   │   ├── Data/
│   │   │   ├── Seed/
│   │   │   │   └── (DatabaseSeeder.cs REMOVED)
│   │   │   └── ...
│   │   └── ...
│   └── AzureBank.Shared/
│       ├── Services/
│       │   ├── Interfaces/
│       │   │   └── IPasswordHasher.cs
│       │   └── Implementations/           ◄── NEW FOLDER
│       │       └── PasswordHasher.cs      ◄── MOVED HERE
│       └── ...
├── tests/
│   └── AzureBank.Tests/
└── tools/                                  ◄── NEW FOLDER
    └── AzureBank.Seeder/                   ◄── NEW PROJECT
        ├── AzureBank.Seeder.csproj
        ├── Program.cs
        ├── appsettings.json
        ├── Commands/
        │   ├── SeedCommand.cs
        │   └── ResetCommand.cs
        ├── Seeders/
        │   ├── ISeeder.cs
        │   ├── SeederOrchestrator.cs
        │   ├── RoleSeeder.cs
        │   ├── UserSeeder.cs
        │   ├── AccountSeeder.cs
        │   └── TransactionSeeder.cs
        └── Extensions/
            └── ServiceCollectionExtensions.cs
```

---

## 4. Implementation Plan

### PHASE 1: Move PasswordHasher to Shared (PREREQUISITE)

> **Note:** This phase is implemented FIRST as a separate step to ensure the project works before creating the Seeder Tool.

#### 1.1 Create Implementations Folder
```
src/AzureBank.Shared/Services/Implementations/
```

#### 1.2 Move PasswordHasher.cs
- **From:** `src/AzureBank.Api/Services/Implementations/PasswordHasher.cs`
- **To:** `src/AzureBank.Shared/Services/Implementations/PasswordHasher.cs`

#### 1.3 Update Namespace
```csharp
// OLD
namespace AzureBank.Api.Services.Implementations;

// NEW
namespace AzureBank.Shared.Services.Implementations;
```

#### 1.4 Add NuGet Package to Shared
```xml
<PackageReference Include="Konscious.Security.Cryptography.Argon2" />
```

#### 1.5 Update DI Registration in Api
```csharp
// In Api/Extensions/ServiceCollectionExtensions.cs
using AzureBank.Shared.Services.Implementations;

services.AddScoped<IPasswordHasher, PasswordHasher>();
```

#### 1.6 Verify Build
```bash
dotnet build AzureBank.slnx
```

---

### PHASE 2: Create Seeder Tool Project Structure

#### 2.1 Create Directory Structure
```bash
mkdir tools
mkdir tools/AzureBank.Seeder
mkdir tools/AzureBank.Seeder/Commands
mkdir tools/AzureBank.Seeder/Seeders
mkdir tools/AzureBank.Seeder/Extensions
```

#### 2.2 Create Project File
**File:** `tools/AzureBank.Seeder/AzureBank.Seeder.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>AzureBank.Seeder</RootNamespace>
    <AssemblyName>azurebank-seeder</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AzureBank.Api\AzureBank.Api.csproj" />
    <ProjectReference Include="..\..\src\AzureBank.Infrastructure\AzureBank.Infrastructure.csproj" />
    <ProjectReference Include="..\..\src\AzureBank.Shared\AzureBank.Shared.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="System.CommandLine" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Serilog.Extensions.Hosting" />
    <PackageReference Include="Serilog.Sinks.Console" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Update="appsettings.Development.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

#### 2.3 Add to Solution
```bash
dotnet sln AzureBank.slnx add tools/AzureBank.Seeder/AzureBank.Seeder.csproj
```

#### 2.4 Update Directory.Packages.props
```xml
<PackageVersion Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
```

---

### PHASE 3: Implement Seeder Components

#### 3.1 Create ISeeder Interface
**File:** `tools/AzureBank.Seeder/Seeders/ISeeder.cs`

```csharp
namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Interface for all database seeders.
/// Seeders are executed in Order sequence.
/// </summary>
public interface ISeeder
{
    /// <summary>
    /// Display name for logging.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Execution order (lower = earlier).
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Execute the seeding operation.
    /// </summary>
    Task SeedAsync(CancellationToken cancellationToken = default);
}
```

#### 3.2 Create SeederOrchestrator
**File:** `tools/AzureBank.Seeder/Seeders/SeederOrchestrator.cs`

```csharp
namespace AzureBank.Seeder.Seeders;

/// <summary>
/// Orchestrates all seeders in order.
/// </summary>
public class SeederOrchestrator
{
    private readonly IEnumerable<ISeeder> _seeders;
    private readonly ILogger<SeederOrchestrator> _logger;

    public SeederOrchestrator(
        IEnumerable<ISeeder> seeders,
        ILogger<SeederOrchestrator> logger)
    {
        _seeders = seeders.OrderBy(s => s.Order);
        _logger = logger;
    }

    public async Task SeedAllAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting database seeding...");

        foreach (var seeder in _seeders)
        {
            _logger.LogInformation("Running {Seeder}...", seeder.Name);
            await seeder.SeedAsync(ct);
            _logger.LogInformation("Completed {Seeder}", seeder.Name);
        }

        _logger.LogInformation("Database seeding completed successfully");
    }
}
```

#### 3.3 Create RoleSeeder
**File:** `tools/AzureBank.Seeder/Seeders/RoleSeeder.cs`

```csharp
namespace AzureBank.Seeder.Seeders;

public class RoleSeeder : ISeeder
{
    private readonly RoleManager<IdentityRole<Guid>> _roleManager;
    private readonly ILogger<RoleSeeder> _logger;

    public string Name => "RoleSeeder";
    public int Order => 1;

    public RoleSeeder(
        RoleManager<IdentityRole<Guid>> roleManager,
        ILogger<RoleSeeder> logger)
    {
        _roleManager = roleManager;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        string[] roles = ["User", "Admin"];

        foreach (var roleName in roles)
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
            {
                var result = await _roleManager.CreateAsync(
                    new IdentityRole<Guid> { Name = roleName });

                if (result.Succeeded)
                    _logger.LogInformation("Created role: {Role}", roleName);
                else
                    _logger.LogError("Failed to create role {Role}: {Errors}",
                        roleName, string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
    }
}
```

#### 3.4 Create UserSeeder
**File:** `tools/AzureBank.Seeder/Seeders/UserSeeder.cs`

```csharp
namespace AzureBank.Seeder.Seeders;

public class UserSeeder : ISeeder
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IPasswordHasher _pinHasher;
    private readonly SeedDataOptions _seedOptions;
    private readonly ILogger<UserSeeder> _logger;

    public string Name => "UserSeeder";
    public int Order => 2;

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

    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await _userManager.Users.AnyAsync(ct))
        {
            _logger.LogInformation("Users already exist. Skipping.");
            return;
        }

        var pinHash = _pinHasher.HashPin(_seedOptions.DefaultPin);

        var seedUsers = new[]
        {
            ("johnsmith", "john@example.com", "John", "Smith", "User"),
            ("janesmith", "jane@example.com", "Jane", "Smith", "User"),
            ("mikebrown", "mike@example.com", "Mike", "Brown", "User"),
            ("admin", _seedOptions.AdminEmail, "Admin", "User", "Admin")
        };

        foreach (var (azureTag, email, firstName, lastName, role) in seedUsers)
        {
            var user = new ApplicationUser
            {
                UserName = azureTag,
                Email = email,
                EmailConfirmed = true,
                AzureTag = azureTag,
                PinHash = pinHash,
                FirstName = firstName,
                LastName = lastName
            };

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
}
```

#### 3.5 Create AccountSeeder
**File:** `tools/AzureBank.Seeder/Seeders/AccountSeeder.cs`

(Similar pattern - extracts CreateAccounts logic from DatabaseSeeder)

#### 3.6 Create TransactionSeeder
**File:** `tools/AzureBank.Seeder/Seeders/TransactionSeeder.cs`

(Similar pattern - extracts CreateTransactions logic from DatabaseSeeder)

---

### PHASE 4: Implement CLI Commands

#### 4.1 Create SeedCommand
**File:** `tools/AzureBank.Seeder/Commands/SeedCommand.cs`

```csharp
namespace AzureBank.Seeder.Commands;

public static class SeedCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("seed", "Seed the database with test data");

        var forceOption = new Option<bool>(
            aliases: ["--force", "-f"],
            description: "Force seeding even if data exists");

        command.AddOption(forceOption);

        command.SetHandler(async (force) =>
        {
            using var scope = services.CreateScope();
            var orchestrator = scope.ServiceProvider
                .GetRequiredService<SeederOrchestrator>();

            await orchestrator.SeedAllAsync();

            Console.WriteLine("✅ Database seeded successfully!");
        }, forceOption);

        return command;
    }
}
```

#### 4.2 Create ResetCommand
**File:** `tools/AzureBank.Seeder/Commands/ResetCommand.cs`

```csharp
namespace AzureBank.Seeder.Commands;

public static class ResetCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("reset", "Reset database and reseed");

        var confirmOption = new Option<bool>(
            aliases: ["--confirm", "-y"],
            description: "Skip confirmation prompt");

        command.AddOption(confirmOption);

        command.SetHandler(async (confirm) =>
        {
            if (!confirm)
            {
                Console.Write("⚠️  This will DELETE all data. Continue? (y/N): ");
                var response = Console.ReadLine();
                if (response?.ToLower() != "y") return;
            }

            using var scope = services.CreateScope();
            var context = scope.ServiceProvider
                .GetRequiredService<AzureBankDbContext>();

            await context.Database.EnsureDeletedAsync();
            await context.Database.MigrateAsync();

            var orchestrator = scope.ServiceProvider
                .GetRequiredService<SeederOrchestrator>();
            await orchestrator.SeedAllAsync();

            Console.WriteLine("✅ Database reset and reseeded!");
        }, confirmOption);

        return command;
    }
}
```

---

### PHASE 5: Configure Entry Point

#### 5.1 Create Program.cs
**File:** `tools/AzureBank.Seeder/Program.cs`

```csharp
using System.CommandLine;
using AzureBank.Seeder.Commands;
using AzureBank.Seeder.Extensions;
using Microsoft.Extensions.Hosting;
using Serilog;

// Build host
var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .WriteTo.Console()
    .CreateLogger();

builder.Services.AddSerilog();

// Register services
builder.Services.AddSeederServices(builder.Configuration, builder.Environment);

var host = builder.Build();

// Build CLI
var rootCommand = new RootCommand("AzureBank Database Seeder Tool")
{
    Description = "CLI tool for seeding and managing the AzureBank database"
};

rootCommand.AddCommand(SeedCommand.Create(host.Services));
rootCommand.AddCommand(ResetCommand.Create(host.Services));

return await rootCommand.InvokeAsync(args);
```

#### 5.2 Create ServiceCollectionExtensions
**File:** `tools/AzureBank.Seeder/Extensions/ServiceCollectionExtensions.cs`

```csharp
namespace AzureBank.Seeder.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSeederServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Add Infrastructure (DbContext, Identity)
        services.AddInfrastructure(configuration, environment);

        // Add PasswordHasher (now from Shared)
        services.AddScoped<IPasswordHasher, PasswordHasher>();

        // Add Seed Options
        services.Configure<SeedDataOptions>(
            configuration.GetSection(SeedDataOptions.SectionName));

        // Register all seeders
        services.AddScoped<ISeeder, RoleSeeder>();
        services.AddScoped<ISeeder, UserSeeder>();
        services.AddScoped<ISeeder, AccountSeeder>();
        services.AddScoped<ISeeder, TransactionSeeder>();

        // Register orchestrator
        services.AddScoped<SeederOrchestrator>();

        return services;
    }
}
```

#### 5.3 Create appsettings.json
**File:** `tools/AzureBank.Seeder/appsettings.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=AzureBank;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "SeedData": {
    "DefaultPassword": "Test123!",
    "DefaultPin": "123456",
    "AdminEmail": "admin@azurebank.dev"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning"
      }
    }
  }
}
```

---

### PHASE 6: Clean Up Old Seeding Code

#### 6.1 Delete DatabaseSeeder
```bash
del src\AzureBank.Infrastructure\Data\Seed\DatabaseSeeder.cs
```

#### 6.2 Remove from Infrastructure ServiceCollectionExtensions
**File:** `src/AzureBank.Infrastructure/Extensions/ServiceCollectionExtensions.cs`

Remove line:
```csharp
services.AddScoped<DatabaseSeeder>();
```

#### 6.3 Remove WebApplicationExtensions Seeding
**File:** `src/AzureBank.Api/Extensions/WebApplicationExtensions.cs`

Remove entire `SeedDatabaseAsync()` method.

#### 6.4 Remove Seeding Call from Program.cs
**File:** `src/AzureBank.Api/Program.cs`

Remove (already commented):
```csharp
//await app.SeedDatabaseAsync();
```

---

## 5. Testing & Validation

### 5.1 Build Verification
```bash
# Build entire solution
dotnet build AzureBank.slnx

# Build seeder specifically
dotnet build tools/AzureBank.Seeder/AzureBank.Seeder.csproj
```

### 5.2 CLI Help Test
```bash
dotnet run --project tools/AzureBank.Seeder -- --help
```

**Expected output:**
```
AzureBank Database Seeder Tool

Usage:
  azurebank-seeder [command] [options]

Commands:
  seed   Seed the database with test data
  reset  Reset database and reseed
```

### 5.3 Seed Command Test
```bash
dotnet run --project tools/AzureBank.Seeder -- seed
```

**Expected output:**
```
[INF] Starting database seeding...
[INF] Running RoleSeeder...
[INF] Created role: User
[INF] Created role: Admin
[INF] Completed RoleSeeder
[INF] Running UserSeeder...
[INF] Created user: john@example.com with role User
[INF] Created user: jane@example.com with role User
[INF] Created user: mike@example.com with role User
[INF] Created user: admin@azurebank.dev with role Admin
[INF] Completed UserSeeder
[INF] Running AccountSeeder...
[INF] Completed AccountSeeder
[INF] Running TransactionSeeder...
[INF] Completed TransactionSeeder
[INF] Database seeding completed successfully
✅ Database seeded successfully!
```

### 5.4 Reset Command Test
```bash
dotnet run --project tools/AzureBank.Seeder -- reset --confirm
```

### 5.5 Verify Api Works
```bash
# Start API
dotnet run --project src/AzureBank.Api

# Test login with seeded user
curl -X POST http://localhost:5068/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"john@example.com","password":"Test123!"}'
```

### 5.6 Verify No Circular Dependencies
```bash
# Infrastructure should build WITHOUT Api reference
dotnet build src/AzureBank.Infrastructure/AzureBank.Infrastructure.csproj
```

---

## 6. References

### 6.1 Related Documents
- [ADR-0003: Argon2id Password Hashing](../docs/adr/0003-argon2id-password-hashing.md)
- [ADR-0007: FluentValidation](../docs/adr/0007-fluentvalidation.md)

### 6.2 External Resources
- [System.CommandLine Documentation](https://learn.microsoft.com/en-us/dotnet/standard/commandline/)
- [Clean Architecture by Uncle Bob](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html)
- [EF Core Seeding](https://learn.microsoft.com/en-us/ef/core/modeling/data-seeding)

---

## Appendix A: CLI Usage Reference

### A.1 Seed Command
```bash
# Basic seeding
dotnet run --project tools/AzureBank.Seeder -- seed

# Force seeding (ignore existing data checks)
dotnet run --project tools/AzureBank.Seeder -- seed --force
```

### A.2 Reset Command
```bash
# Interactive reset (prompts for confirmation)
dotnet run --project tools/AzureBank.Seeder -- reset

# Non-interactive reset (CI/CD friendly)
dotnet run --project tools/AzureBank.Seeder -- reset --confirm
```

### A.3 Help
```bash
# General help
dotnet run --project tools/AzureBank.Seeder -- --help

# Command-specific help
dotnet run --project tools/AzureBank.Seeder -- seed --help
dotnet run --project tools/AzureBank.Seeder -- reset --help
```

---

*End of Document*
