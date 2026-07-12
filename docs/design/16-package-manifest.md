# Package Dependency Manifest
## Bank Account Management System

**Document Version**: 4.0
**Created**: 2025-12-16
**Updated**: 2026-01-08
**Status**: FINALIZED - BUN + VITE STACK (Phase 6 Technology Evaluation)

---

## Version Policy

**PRINCIPLE**: Always use the latest stable version or latest LTS version available.

All versions listed are current as of December 2025. Run `npm outdated` and `dotnet list package --outdated` before implementation to verify.

---

## Changelog (v4.0 - Phase 6 Technology Evaluation)

### Major Changes in v4.0

| Change Type | Package/Tool | From | To | Reason |
|-------------|--------------|------|-----|--------|
| **SWITCHED** | Package Manager | npm | **Bun** | Native TS, 17x faster than pnpm, all-in-one |
| **UPDATED** | Build Tool | Vite 5.x | **Vite 6.x + Bun runtime** | Maximum speed with `bunx --bun vite` |
| **UPDATED** | React Router | ^6.28.0 | **^7.1.1** | Latest version |
| **ADDED** | SweetAlert2 | - | **^11.15.2** | Confirmation dialogs, rich modals |
| **ADDED** | sweetalert2-react-content | - | **^5.1.0** | React JSX in SweetAlert2 |
| **ADDED** | use-debounce | - | **^10.0.6** | Search/filter debouncing |
| **NOT ADDED** | react-toastify | - | - | FluentUI Toast built-in covers needs |
| **ADDED** | Microsoft.AspNetCore.Identity.EntityFrameworkCore | - | **10.0.0** | User/role management, consistent with MangoFusion_API |
| **CHANGED** | All "Recommended" packages | Recommended | **Yes** | User approved all recommended packages |

### Changes from v3.0

| Change Type | Package | From | To | Reason |
|-------------|---------|------|-----|--------|
| **SWITCHED** | JWT Library | `System.IdentityModel.Tokens.Jwt` | `Microsoft.IdentityModel.JsonWebTokens` | 30% better performance, async support, modern API |
| **UPDATED** | Scalar.AspNetCore | 1.2.74 | 2.11.5 | Latest version (Dec 2025) |
| **NOT ADDED** | Newtonsoft.Json | - | - | System.Text.Json is default in .NET 10, 2-3x faster |
| **NOT ADDED** | Microsoft.AspNetCore.Identity.EntityFrameworkCore | - | - | Custom JWT preferred for API-first SPAs |

### Rationale Summary

1. **Microsoft.IdentityModel.JsonWebTokens > System.IdentityModel.Tokens.Jwt**
   - Microsoft's official replacement for JwtSecurityTokenHandler
   - 30% performance improvement
   - Async token validation support
   - Better resilience with "last known good" metadata
   - ASP.NET Core 8+ uses this by default

2. **System.Text.Json > Newtonsoft.Json**
   - Built into .NET (no extra dependency)
   - 2-3x faster performance
   - 50% less memory usage
   - Better AOT/trimming support
   - ASP.NET Core default since .NET Core 3.1

3. **ASP.NET Core Identity + JWT** (Updated Decision)
   - Consistent with MangoFusion_API approach
   - Identity handles user management, roles, password hashing
   - JWT used for stateless authentication
   - BFF stores JWT server-side (HTTP-only session cookies)
   - Enterprise-standard, battle-tested solution

---

## Overview

This document contains the complete list of all npm packages (frontend) and NuGet packages (backend) required for the project, with justifications for each.

---

## Frontend Packages (npm)

### Core Dependencies

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `react` | ^19.2.3 | UI framework (latest Dec 2025) | Yes |
| `react-dom` | ^19.2.3 | React DOM rendering | Yes |
| `typescript` | ^5.7.2 | Type safety (latest) | Yes |

### UI Components

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `@fluentui/react-components` | ^9.72.8 | FluentUI v9 component library (latest Dec 2025) | Yes |
| `@fluentui/react-icons` | ^2.0.270 | FluentUI icon library (latest) | Yes |

### State Management

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `@reduxjs/toolkit` | ^2.11.2 | Redux with best practices (latest Dec 2025) | Yes |
| `react-redux` | ^9.2.0 | React bindings for Redux | Yes |

### Routing

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `react-router-dom` | ^6.28.0 | Client-side routing | Yes |

### HTTP Client

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `axios` | ^1.7.9 | HTTP client with interceptors | Yes |

### Form Handling

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `react-hook-form` | ^7.54.0 | Form state management | Yes |
| `@hookform/resolvers` | ^3.9.0 | Validation resolvers | Yes |
| `zod` | ^3.24.0 | Schema validation | Yes |

### Utilities

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `date-fns` | ^4.1.0 | Date manipulation | Yes |
| `clsx` | ^2.1.0 | Conditional CSS classes | Yes |
| `use-debounce` | ^10.0.6 | Debouncing hooks (search, filters) | Yes |

### Notifications & Dialogs

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `sweetalert2` | ^11.15.2 | Confirmation dialogs, rich modals | Yes |
| `sweetalert2-react-content` | ^5.1.0 | React JSX support for SweetAlert2 | Yes |

**Note**: FluentUI Toast (`@fluentui/react-components`) is used for success/error/info toasts. SweetAlert2 is used for confirmation dialogs and critical warnings.

### Development Dependencies

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `vite` | ^5.4.0 | Build tool & dev server | Yes |
| `@vitejs/plugin-react` | ^4.3.0 | React plugin for Vite | Yes |
| `msw` | ^2.7.0 | Mock Service Worker | Yes |
| `@types/react` | ^19.0.0 | TypeScript types for React | Yes |
| `@types/react-dom` | ^19.0.0 | TypeScript types for ReactDOM | Yes |
| `eslint` | ^9.17.0 | Linting | Yes |
| `@eslint/js` | ^9.17.0 | ESLint JS config | Yes |
| `typescript-eslint` | ^8.18.0 | TypeScript ESLint plugin | Yes |
| `eslint-plugin-react-hooks` | ^5.1.0 | React hooks linting | Yes |
| `eslint-plugin-react-refresh` | ^0.4.0 | React refresh linting | Yes |
| `prettier` | ^3.4.0 | Code formatting | Yes |

---

### Frontend package.json Template (Bun + Vite)

```json
{
  "name": "azurebank-frontend",
  "private": true,
  "version": "1.0.0",
  "type": "module",
  "scripts": {
    "dev": "bunx --bun vite",
    "build": "bunx vite build",
    "preview": "bunx vite preview",
    "lint": "bunx eslint .",
    "type-check": "bunx tsc --noEmit",
    "format": "bunx prettier --write \"src/**/*.{ts,tsx}\"",
    "format:check": "bunx prettier --check \"src/**/*.{ts,tsx}\""
  },
  "dependencies": {
    "@fluentui/react-components": "^9.72.8",
    "@fluentui/react-icons": "^2.0.270",
    "@hookform/resolvers": "^3.9.1",
    "@reduxjs/toolkit": "^2.11.2",
    "axios": "^1.7.9",
    "clsx": "^2.1.1",
    "date-fns": "^4.1.0",
    "react": "^19.2.3",
    "react-dom": "^19.2.3",
    "react-hook-form": "^7.54.2",
    "react-redux": "^9.2.0",
    "react-router-dom": "^7.1.1",
    "sweetalert2": "^11.15.2",
    "sweetalert2-react-content": "^5.1.0",
    "use-debounce": "^10.0.6",
    "zod": "^3.24.1"
  },
  "devDependencies": {
    "@eslint/js": "^9.17.0",
    "@types/react": "^19.2.0",
    "@types/react-dom": "^19.2.0",
    "@vitejs/plugin-react": "^4.3.4",
    "eslint": "^9.17.0",
    "eslint-plugin-react-hooks": "^5.1.0",
    "eslint-plugin-react-refresh": "^0.4.16",
    "msw": "^2.7.0",
    "prettier": "^3.4.2",
    "typescript": "^5.7.2",
    "typescript-eslint": "^8.18.2",
    "vite": "^6.0.5"
  },
  "msw": {
    "workerDirectory": ["public"]
  }
}
```

---

## Backend Packages (NuGet)

### Core Framework

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `Microsoft.AspNetCore.OpenApi` | 10.0.1 | OpenAPI support (.NET 10 LTS) | Yes |
| `Microsoft.EntityFrameworkCore` | 10.0.0 | ORM framework (EF Core 10 for .NET 10) | Yes |
| `Microsoft.EntityFrameworkCore.SqlServer` | 10.0.0 | SQL Server provider | Yes |
| `Microsoft.EntityFrameworkCore.Tools` | 10.0.0 | EF migrations CLI | Yes |
| `Microsoft.EntityFrameworkCore.Design` | 10.0.0 | EF design-time | Yes |

### Authentication & Security

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | 10.0.0 | ASP.NET Core Identity with EF Core | Yes |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | 10.0.1 | JWT authentication (.NET 10) | Yes |
| `Microsoft.IdentityModel.JsonWebTokens` | 8.15.0 | JWT token handling (modern, 30% faster) | Yes |

**Note**: Identity handles password hashing (configurable to Argon2id), user management, and role management. JWT is used for stateless token-based auth with the BFF pattern.

### API Documentation

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `Scalar.AspNetCore` | 2.11.5 | Modern API documentation (latest Dec 2025) | Yes |

### Validation

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `FluentValidation` | 11.11.0 | Request validation | Yes |
| `FluentValidation.AspNetCore` | 11.3.0 | ASP.NET Core integration | Yes |

### Logging

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `Serilog` | 4.2.0 | Structured logging | Yes |
| `Serilog.AspNetCore` | 9.0.0 | ASP.NET Core integration | Yes |
| `Serilog.Sinks.Console` | 6.0.0 | Console output | Yes |
| `Serilog.Sinks.File` | 6.0.0 | File output | Yes |
| `Serilog.Enrichers.Environment` | 3.0.1 | Environment enricher | Yes |
| `Serilog.Enrichers.Thread` | 4.0.0 | Thread enricher | Yes |

### Object Mapping

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `Riok.Mapperly` | 4.0.1 | Compile-time object mapping (NOT AutoMapper) | Yes |

**Why Mapperly over AutoMapper?**
- **License**: MIT (free forever) vs AutoMapper's paid LTS license
- **Performance**: Compile-time source generation, zero reflection
- **AOT Compatible**: Full Native AOT support
- **Type Safety**: Compile-time errors instead of runtime errors
- **.NET 10 Support**: Excellent compatibility

See [17-cross-cutting-concerns.md](17-cross-cutting-concerns.md) Section 5 for detailed mapper implementations.

### Testing (Optional)

| Package | Version | Purpose | Required |
|---------|---------|---------|----------|
| `xunit` | 2.9.0 | Testing framework | Optional |
| `xunit.runner.visualstudio` | 2.8.0 | VS test runner | Optional |
| `Moq` | 4.20.0 | Mocking library | Optional |
| `FluentAssertions` | 7.0.0 | Assertion library | Optional |
| `Microsoft.EntityFrameworkCore.InMemory` | 9.0.0 | In-memory database for tests | Optional |

---

### Backend .csproj Template

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>14.0</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <!-- Core -->
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.1" />

    <!-- Entity Framework Core 10 -->
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Tools" Version="10.0.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>

    <!-- Identity & Authentication -->
    <PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.0" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.1" />
    <PackageReference Include="Microsoft.IdentityModel.JsonWebTokens" Version="8.15.0" />

    <!-- API Documentation -->
    <PackageReference Include="Scalar.AspNetCore" Version="2.11.5" />

    <!-- Validation -->
    <PackageReference Include="FluentValidation" Version="11.11.0" />
    <PackageReference Include="FluentValidation.AspNetCore" Version="11.3.0" />

    <!-- Logging -->
    <PackageReference Include="Serilog" Version="4.2.0" />
    <PackageReference Include="Serilog.AspNetCore" Version="9.0.0" />
    <PackageReference Include="Serilog.Sinks.Console" Version="6.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
    <PackageReference Include="Serilog.Enrichers.Environment" Version="3.0.1" />
    <PackageReference Include="Serilog.Enrichers.Thread" Version="4.0.0" />

    <!-- Object Mapping (NOT AutoMapper - license issues) -->
    <PackageReference Include="Riok.Mapperly" Version="4.0.1" />
  </ItemGroup>

</Project>
```

---

## Package Justifications

### Frontend Justifications

#### Why Axios over Fetch?
- **Interceptors**: Can add auth token to all requests globally
- **Error Handling**: Better error object structure
- **Cancellation**: Built-in request cancellation
- **Transform**: Automatic JSON transformation
- **Compatibility**: Works well with RTK Query baseQuery

#### Why react-hook-form + zod?
- **Performance**: Minimal re-renders
- **Type Safety**: Zod provides TypeScript inference
- **Validation**: Schema-based validation
- **FluentUI Integration**: Works well with FluentUI form controls

#### Why date-fns over moment.js?
- **Tree-shaking**: Import only what you need
- **Size**: Much smaller bundle size
- **Modern**: Uses native Date objects
- **Active**: Actively maintained

#### Why MSW?
- **Realistic**: Intercepts at network level
- **Same Code**: Works in dev and tests
- **API Contracts**: Enforces API contract compliance
- **Parallel Development**: Frontend doesn't wait for backend

### Backend Justifications

#### Password Hashing with Identity
- **Default**: Identity uses PBKDF2 by default
- **Configurable**: Can configure Identity to use Argon2id if needed
- **OWASP Compliant**: Built-in hashing meets security standards
- **Note**: `Konscious.Security.Cryptography.Argon2` is optional if custom Argon2 is required

#### Why Scalar over Swagger?
- **Modern UI**: Cleaner, more modern interface
- **Better UX**: Improved API exploration experience
- **Dark Mode**: Built-in dark mode support
- **Testing**: Better API testing interface
- **Experience**: Team member familiar with Scalar

#### Why FluentValidation?
- **Separation**: Validation logic separate from DTOs
- **Testable**: Easy to unit test
- **Readable**: Fluent API is readable
- **Integration**: Seamless ASP.NET Core integration

#### Why Serilog?
- **Structured**: Logs as structured data, not just text
- **Sinks**: Multiple output destinations
- **Enrichers**: Add context automatically
- **Performance**: Optimized for production

---

## Installation Commands

### Package Manager: Bun

**Why Bun?** (See [PHASE-6-TECHNOLOGY-EVALUATION.md](PHASE-6-TECHNOLOGY-EVALUATION.md))
- Native TypeScript support (no tsc compilation for scripts)
- 17x faster than pnpm, 29x faster than npm for cached installs
- All-in-one: package manager + runtime + bundler + test runner
- Works seamlessly with Vite

```bash
# Install Bun (Windows PowerShell)
powershell -c "irm bun.sh/install.ps1 | iex"

# Verify installation
bun --version
```

### Frontend Setup (Bun)

```bash
# Create Vite project with React + TypeScript
bun create vite frontend --template react-ts
cd frontend

# Install production dependencies
bun add @fluentui/react-components @fluentui/react-icons
bun add @reduxjs/toolkit react-redux
bun add react-router-dom
bun add axios
bun add react-hook-form @hookform/resolvers zod
bun add date-fns clsx
bun add sweetalert2 sweetalert2-react-content
bun add use-debounce

# Install dev dependencies
bun add -d msw
bun add -d eslint @eslint/js typescript-eslint
bun add -d eslint-plugin-react-hooks eslint-plugin-react-refresh
bun add -d prettier

# Initialize MSW
bunx msw init public/ --save

# Start development server (maximum speed)
bunx --bun vite
```

### Alternative: npm Setup (Legacy)

```bash
# Create Vite project with React + TypeScript
npm create vite@latest frontend -- --template react-ts
cd frontend

# Install production dependencies
npm install @fluentui/react-components @fluentui/react-icons
npm install @reduxjs/toolkit react-redux
npm install react-router-dom
npm install axios
npm install react-hook-form @hookform/resolvers zod
npm install date-fns clsx
npm install sweetalert2 sweetalert2-react-content
npm install use-debounce

# Install dev dependencies
npm install -D msw
npm install -D eslint @eslint/js typescript-eslint
npm install -D eslint-plugin-react-hooks eslint-plugin-react-refresh
npm install -D prettier

# Initialize MSW
npx msw init public/ --save
```

### Backend Setup

```bash
# Create solution and project
dotnet new sln -n BankApp
dotnet new webapi -n BankApp.API -o src/BankApp.API
dotnet sln add src/BankApp.API

# Add packages
cd src/BankApp.API

# Entity Framework
dotnet add package Microsoft.EntityFrameworkCore --version 9.0.0
dotnet add package Microsoft.EntityFrameworkCore.SqlServer --version 9.0.0
dotnet add package Microsoft.EntityFrameworkCore.Tools --version 9.0.0
dotnet add package Microsoft.EntityFrameworkCore.Design --version 9.0.0

# Identity & Authentication
dotnet add package Microsoft.AspNetCore.Identity.EntityFrameworkCore --version 10.0.0
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer --version 10.0.1
dotnet add package Microsoft.IdentityModel.JsonWebTokens --version 8.15.0

# API Documentation
dotnet add package Scalar.AspNetCore --version 2.11.5

# Validation
dotnet add package FluentValidation --version 11.11.0
dotnet add package FluentValidation.AspNetCore --version 11.3.0

# Logging
dotnet add package Serilog --version 4.2.0
dotnet add package Serilog.AspNetCore --version 9.0.0
dotnet add package Serilog.Sinks.Console --version 6.0.0
dotnet add package Serilog.Sinks.File --version 6.0.0
dotnet add package Serilog.Enrichers.Environment --version 3.0.1
dotnet add package Serilog.Enrichers.Thread --version 4.0.0

# Object Mapping (NOT AutoMapper - license issues)
dotnet add package Riok.Mapperly --version 4.0.1
```

---

## Version Compatibility Matrix

| Frontend Package | Requires |
|-----------------|----------|
| @fluentui/react-components 9.x | React 18+ |
| @reduxjs/toolkit 2.x | React 18+ |
| react-router-dom 6.x | React 18+ |
| msw 2.x | Node 18+ |
| vite 5.x | Node 18+ |

| Backend Package | Requires |
|-----------------|----------|
| .NET 10 | .NET SDK 10.0 |
| EF Core 9.x | .NET 8+ |
| Scalar.AspNetCore | .NET 8+ |

---

## Security Considerations

### Frontend
- **No sensitive data in localStorage** - Tokens in memory only
- **CSP headers** - Configure in production
- **Dependencies audit** - Run `npm audit` regularly

### Backend
- **Package vulnerabilities** - Run `dotnet list package --vulnerable`
- **Outdated packages** - Run `dotnet list package --outdated`
- **NuGet sources** - Use only trusted sources

---

## Update Strategy

1. **Monthly**: Check for security updates
2. **Quarterly**: Evaluate minor version updates
3. **Annually**: Plan major version migrations
4. **Always**: Read changelogs before updating

---

## Package Evaluation Report (December 2025)

### Packages Evaluated from MangaFusion_API Project

Based on the screenshot provided, the following packages were evaluated:

#### 1. Newtonsoft.Json

**Decision**: ❌ NOT ADDED

**Analysis**:
| Aspect | System.Text.Json (Built-in) | Newtonsoft.Json |
|--------|----------------------------|-----------------|
| Performance | 2-3x faster | Baseline |
| Memory | 50% less allocation | Higher overhead |
| Bundle | Built into .NET | Extra NuGet package |
| AOT Support | Excellent | Limited |
| Default | Yes (.NET Core 3.1+) | No |

**Rationale**:
- System.Text.Json is the default JSON serializer in ASP.NET Core since .NET Core 3.1
- .NET 10 adds `JsonSerializerOptions.Strict` and `PipeReader` deserialization
- Our API is straightforward JSON - no need for JObject, JSONPath, or LINQ-to-JSON
- Performance critical for banking transaction throughput

**When Newtonsoft.Json would be needed**:
- Heavy use of JSONPath queries
- LINQ-to-JSON (JObject, JToken manipulation)
- Complex polymorphic serialization
- Legacy .NET Framework compatibility

**Sources**:
- [Microsoft Migration Guide](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/migrate-from-newtonsoft)
- [Performance Benchmark 2025](https://jkrussell.dev/blog/system-text-json-vs-newtonsoft-json-benchmark/)

---

#### 2. System.IdentityModel.Tokens.Jwt vs Microsoft.IdentityModel.JsonWebTokens

**Decision**: ✅ SWITCHED to Microsoft.IdentityModel.JsonWebTokens

**Analysis**:
| Aspect | Microsoft.IdentityModel.JsonWebTokens | System.IdentityModel.Tokens.Jwt |
|--------|---------------------------------------|--------------------------------|
| Generation | New (recommended) | Legacy |
| Performance | 30% faster | Baseline |
| Async Support | Full async validation | Limited |
| Resilience | "Last known good" metadata | None |
| .NET Default | ASP.NET Core 8+ | Pre-.NET 8 |

**Key Classes**:
- **Old**: `JwtSecurityToken`, `JwtSecurityTokenHandler`
- **New**: `JsonWebToken`, `JsonWebTokenHandler`

**Breaking Change in ASP.NET Core 8.0**:
The `TokenValidatedContext.SecurityToken` now returns `JsonWebToken` instead of `JwtSecurityToken` by default.

**Sources**:
- [NuGet Package](https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens)
- [Breaking Change Documentation](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/8.0/securitytoken-events)

---

#### 3. Microsoft.AspNetCore.Identity.EntityFrameworkCore

**Decision**: ✅ ADDED (User Request - Consistent with MangoFusion_API)

**Version**: 10.0.0 (for .NET 10)

**Analysis**:
| Aspect | Without Identity | With Identity |
|--------|-----------------|---------------|
| Password Hashing | Manual implementation | Built-in (configurable) |
| User CRUD | Manual repository | `UserManager<ApplicationUser>` |
| Role Management | Manual | `RoleManager<IdentityRole>` |
| Password Validation | Manual rules | Built-in + customizable |
| Lockout on Failed Login | Manual | Built-in |
| Email Confirmation | Manual | Built-in (future) |
| Two-Factor Auth | Complex | Built-in (future) |

**Why Identity is RIGHT for Our Project**:

1. **Consistency**: Same patterns as MangoFusion_API project - proven approach
2. **Battle-tested**: Microsoft's production-ready user management
3. **BFF Compatible**: Identity manages users, BFF stores JWT in session cookie
4. **Less Code**: No need to write password hashing, validation, lockout, etc.
5. **Extensibility**: Easy to add 2FA, external providers later

**How Identity Works with BFF Pattern**:
```
Frontend (React) → BFF Gateway (Session cookies)
                        │
                        ├── Uses UserManager.CheckPasswordAsync()
                        ├── Generates JWT internally
                        └── Stores JWT in session (HTTP-only cookie)
                        │
                        ▼
                   Backend API (Validates JWT)
                        │
                        ▼
                   Database (Identity tables + App tables)
```

**Identity Tables Created**:
- `AspNetUsers` - User accounts (extends with ApplicationUser)
- `AspNetRoles` - Roles (Admin, Customer)
- `AspNetUserRoles` - User-role mappings
- `AspNetUserClaims`, `AspNetRoleClaims` - Claims
- `AspNetUserLogins`, `AspNetUserTokens` - External logins (future)

**Reference Implementation**: See `C:\Users\ellen\source\repos\MangoFusion_API`

**Sources**:
- [Identity vs JWT Comparison](https://dev.to/omotola_odumosu_/aspnet-core-identity-vs-jwt-without-identity-53hm)
- [ASP.NET Core Identity Documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity)

---

#### 4. Microsoft.EntityFrameworkCore.Tools & Microsoft.EntityFrameworkCore.SqlServer

**Decision**: ✅ ALREADY INCLUDED

These packages were already in our manifest:
- `Microsoft.EntityFrameworkCore.Tools` - Required for EF migrations CLI
- `Microsoft.EntityFrameworkCore.SqlServer` - Required for SQL Server provider

Both at version 10.0.0 for .NET 10 compatibility.

---

#### 5. Microsoft.AspNetCore.OpenApi

**Decision**: ✅ ALREADY INCLUDED

Already in our manifest at version 10.0.1. Required for OpenAPI/Scalar integration.

---

#### 6. Scalar.AspNetCore

**Decision**: ✅ UPDATED to 2.11.5

Updated from 1.2.74 to latest version 2.11.5 (December 2025).

New features in 2.x:
- Improved performance
- Better TypeScript support
- Enhanced theming options
- Stability indicators support

---

#### 7. Microsoft.AspNetCore.Authentication.JwtBearer

**Decision**: ✅ ALREADY INCLUDED (Confirmed Required)

**Version**: 10.0.1 (latest stable for .NET 10)

**Purpose**: This is the official Microsoft middleware for JWT Bearer authentication in ASP.NET Core. It is **required** for our application.

**What it provides**:
- `JwtBearerDefaults` - Default authentication scheme name
- `JwtBearerEvents` - Hooks for authentication events (OnTokenValidated, OnAuthenticationFailed, etc.)
- `JwtBearerHandler` - Processes JWT tokens from Authorization headers
- `JwtBearerOptions` - Configuration for token validation parameters

**Why it's essential**:
1. Extracts JWT from `Authorization: Bearer <token>` header
2. Validates token signature, expiration, issuer, audience
3. Populates `HttpContext.User` with claims from token
4. Integrates with ASP.NET Core's authentication middleware pipeline

**Relationship with other packages**:
```
Microsoft.AspNetCore.Authentication.JwtBearer (middleware)
    └── Uses: Microsoft.IdentityModel.JsonWebTokens (token handling)
        └── For: Token creation, validation, claims extraction
```

**Configuration Example**:
```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
```

**Sources**:
- [NuGet Package](https://www.nuget.org/packages/Microsoft.AspNetCore.Authentication.JwtBearer)
- [Microsoft Documentation](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication)

---

## Best Practices Adopted

### 1. Dependency Management
- Use latest stable/LTS versions
- Pin major versions with `^` prefix
- Run security audits monthly (`npm audit`, `dotnet list package --vulnerable`)
- Document all package decisions with rationale

### 2. JSON Handling
- Use System.Text.Json for all serialization
- Configure strict mode for API validation
- Use source generators for AOT scenarios if needed

### 3. JWT Authentication
- Use Microsoft.IdentityModel.JsonWebTokens (not the legacy package)
- Implement async token validation
- Use short-lived access tokens (15-30 min)
- Store tokens in memory only (Redux state)

### 4. Database Access
- Use EF Core 10 with SQL Server provider
- Enable parameterized queries (default)
- Use migrations for schema management
- Implement repository pattern for testability

### 5. API Documentation
- Use Scalar for modern API reference UI
- Enable OpenAPI specification generation
- Document all endpoints with proper schemas

---

## New Package Justifications (v4.0)

### Why Bun over npm/pnpm?
- **Native TypeScript**: No `tsc` compilation needed for scripts
- **Speed**: 17x faster than pnpm, 29x faster than npm for cached installs
- **All-in-One**: Package manager + runtime + bundler + test runner
- **Modern**: Built from ground up for 2024+ development patterns
- **Vite Compatible**: Works seamlessly with Vite ([Bun + Vite Guide](https://bun.com/docs/guides/ecosystem/vite))

**Sources**:
- [Bun Package Manager Comparison](https://benjamincrozat.com/bun-package-manager)
- [Better Stack: pnpm vs Bun](https://betterstack.com/community/guides/scaling-nodejs/pnpm-vs-bun-install-vs-yarn/)

### Why SweetAlert2?
- **Rich Modal Experiences**: Confirmations, forms, multi-step dialogs
- **React Integration**: via `sweetalert2-react-content`
- **Accessible**: ARIA support, keyboard navigation
- **Use Cases**: Delete confirmation, transfer confirmation, critical warnings

**Sources**:
- [SweetAlert2 Official](https://sweetalert2.github.io/)
- [React Toast Libraries Comparison 2025](https://blog.logrocket.com/react-toast-libraries-compared-2025/)

### Why use-debounce?
- **Zero Dependencies**: Standalone, TypeScript included
- **Two Hooks**: `useDebounce` (values), `useDebouncedCallback` (functions)
- **Features**: `maxWait`, `cancel`, `flush`, `isPending`
- **Use Cases**: Recipient search, filter inputs, auto-save

**Sources**:
- [use-debounce npm](https://www.npmjs.com/package/use-debounce)

### Why NOT react-toastify?
- **FluentUI Toast Built-in**: Already in `@fluentui/react-components`
- **Design Consistency**: Matches our FluentUI design system
- **No Extra Bundle**: Toaster, Toast, ToastTitle, ToastBody included
- **Accessible**: WCAG compliant out of box

**Sources**:
- [FluentUI Toast Documentation](https://fluent2.microsoft.design/components/web/react/core/toast/usage)

---

**Document Status**: COMPLETE (v4.0 - Phase 6 Technology Evaluation)
