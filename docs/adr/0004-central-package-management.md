# ADR-0004: Central Package Management (CPM)

**Status:** Accepted · **Date:** 2026-01-10 · **Decision Makers:** Vladislav Aleshaev

## Context

The backend is one solution of several projects that share NuGet packages. With a version on every
`PackageReference`, two projects can build against different versions of one package, an upgrade
means editing several `.csproj` files, a version conflict can surface at run time, and auditing
what the solution depends on means reading every project.

## Decision

1. **Every NuGet package version is declared once, in `backend/Directory.Packages.props`, with
   `ManagePackageVersionsCentrally` set to `true`**, because NuGet's own
   [Central Package Management](https://learn.microsoft.com/nuget/consume-packages/central-package-management)
   needs no extra tool, is supported by Visual Studio and VS Code, is Microsoft's own guidance for
   .NET solutions, and can pin the version of a transitive dependency.
2. **A project names a package with no `Version` attribute**, because every project then builds
   against the same version and an upgrade is one edit in one file.
3. **The file groups its packages by purpose, in labelled item groups**, because the whole
   dependency list is then read and reviewed in one place.

## Rejected

- Rejected: a version on each `PackageReference`, because nothing keeps the projects on one version
  and the version of a transitive dependency cannot be pinned.
- Rejected: version variables in `Directory.Build.props`, because the centralisation is manual, IDE
  support is partial, the mechanism is not an official one and it pins no transitive version.
- Rejected: Paket, because it is an extra tool, supported by its community only, with limited IDE
  support.

## Consequences

- Every package a project names resolves to the one version in the central file. A package that
  arrives only as a dependency of another package can still differ between projects: transitive
  pinning (`CentralPackageTransitivePinningEnabled`) is not turned on.
- An upgrade edits one file; every dependency and its version can be reviewed there.
- Project files are simpler: they carry no version attributes. `Directory.Packages.props` is
  committed with the solution.
- It costs flexibility: a project cannot take a different version of a package without an explicit
  override. It needs NuGet 6.2+ and .NET 6+, which .NET 10 meets, and a developer who has
  not met Central Package Management has it to learn.
- Not covered: GitHub's dependency graph reads no version from this file, so no advisory is
  matched against the backend's packages (see [`SECURITY.md`](../../SECURITY.md), "Dependencies").

## Verified by

- `dotnet build backend/AzureBank.slnx`: the solution restores and builds from the central file.
- `grep -rn "PackageReference.*Version=" backend --include=*.csproj` prints nothing.

## Related

ADR-0001.
