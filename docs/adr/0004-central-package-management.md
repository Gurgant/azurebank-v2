# ADR-0004: Central Package Management (CPM)

**Status**: Accepted

**Date**: 2026-01-10

**Decision Makers**: Architecture Team

---

## Context

Managing NuGet package versions across multiple projects in a solution becomes challenging as the solution grows. Without centralization:
- Different projects may use different versions of the same package
- Upgrading packages requires changes in multiple `.csproj` files
- Version conflicts can cause runtime issues
- Auditing package versions is difficult

## Decision Drivers

- **Consistency**: All projects should use the same package versions
- **Maintainability**: Package upgrades should be simple
- **Auditability**: Easy to see all packages and versions in one place
- **Security**: Easier to track and update vulnerable packages

## Considered Options

1. **Central Package Management (Directory.Packages.props)**: NuGet's built-in CPM
2. **Individual PackageReference**: Traditional per-project versioning
3. **Directory.Build.props with variables**: Custom centralized versioning
4. **Paket**: Alternative package manager

## Decision

Use **NuGet Central Package Management** with `Directory.Packages.props` at the solution root.

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <!-- ASP.NET Core -->
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.0-preview.1.25120.3" />
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.0-preview.1.25120.3" />

    <!-- Entity Framework Core -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.0-preview.1.25081.2" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0-preview.1.25081.2" />

    <!-- And all other packages... -->
  </ItemGroup>
</Project>
```

Projects reference packages without versions:

```xml
<!-- AzureBank.Api.csproj -->
<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
  <PackageReference Include="FluentValidation.DependencyInjectionExtensions" />
</ItemGroup>
```

## Rationale

### Why Central Package Management?

1. **Native NuGet Support**: Built into NuGet 6.2+ and .NET 6+
2. **IDE Integration**: Full Visual Studio and VS Code support
3. **No Additional Tools**: Unlike Paket, no extra tooling required
4. **Microsoft Recommended**: Official Microsoft guidance for .NET solutions

### Feature Comparison

| Feature | CPM | Individual | Directory.Build.props | Paket |
|---------|-----|------------|----------------------|-------|
| Version centralization | ✅ Native | ❌ No | ⚠️ Manual | ✅ Yes |
| IDE support | ✅ Full | ✅ Full | ⚠️ Partial | ⚠️ Limited |
| Transitive pinning | ✅ Yes | ❌ No | ❌ No | ✅ Yes |
| No extra tools | ✅ Yes | ✅ Yes | ✅ Yes | ❌ No |
| Microsoft support | ✅ Yes | ✅ Yes | ⚠️ Unofficial | ❌ Community |

### Benefits Realized

1. **Single Source of Truth**: All 25+ packages defined in one file
2. **Easy Upgrades**: Change version once, all projects updated
3. **Dependency Audit**: Simple to review all dependencies
4. **Consistent Builds**: No version mismatches between projects

## Consequences

### Positive

- All projects use identical package versions
- Package upgrades require editing only one file
- Easy to audit all dependencies for security vulnerabilities
- Transitive dependency versions can be pinned
- Better compatibility with Dependabot/Renovate

### Negative

- All projects must use the same version (no per-project overrides without explicit configuration)
- Requires NuGet 6.2+ / .NET 6+ (not an issue for .NET 10)
- Learning curve for developers unfamiliar with CPM

### Neutral

- Project files are simpler (no version attributes)
- `Directory.Packages.props` must be committed to source control

## Implementation

### File Location

```
AzureBank/
└── backend/
    ├── Directory.Packages.props    # Central version definitions
    ├── Directory.Build.props       # Shared project properties
    ├── AzureBank.sln
    └── src/
        └── ...
```

### Package Categories

Packages are organized by category in `Directory.Packages.props`:

1. **ASP.NET Core** - Web framework packages
2. **Entity Framework Core** - ORM packages
3. **Security** - Authentication, hashing
4. **Validation** - FluentValidation
5. **Mapping** - Mapperly
6. **Logging** - Serilog
7. **Documentation** - Scalar
8. **Testing** - xUnit, Moq, FluentAssertions
9. **Infrastructure** - YARP, Testcontainers

## Validation

Success criteria:
- All projects use versions from `Directory.Packages.props`
- No explicit versions in `.csproj` files
- Solution builds successfully
- Package restore works correctly

## Related

- [ADR-0001: BFF Pattern](./0001-bff-pattern.md)
- [Microsoft CPM Documentation](https://learn.microsoft.com/nuget/consume-packages/central-package-management)

---

## References

- [NuGet Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management)
- [.NET SDK CPM Support](https://devblogs.microsoft.com/nuget/introducing-central-package-management/)
- [Transitive Pinning](https://learn.microsoft.com/nuget/consume-packages/central-package-management#transitive-pinning)
