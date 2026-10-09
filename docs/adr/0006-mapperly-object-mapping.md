# ADR-0006: Mapperly Object Mapping

**Status:** Accepted · **Date:** 2026-01-11 · **Decision Makers:** Vladislav Aleshaev

## Context

The API maps domain entities to response DTOs, request DTOs to entities, and internal service
objects to external contracts. Hand-written mapping is verbose and error-prone, and it has to be
kept up as the models change. The mapping must not add to the API's latency, must be checked at
compile time, must be open to a debugger, and must need little configuration.

## Decision

1. **Object mapping is generated at compile time by Mapperly (`Riok.Mapperly`), a source
   generator**, because it emits plain C# with no runtime reflection and no expression compilation:
   a mapping costs what hand-written code costs, a mapping that cannot be generated is a build
   error, and the generated code can be stepped through. It works with Native AOT and trimming.
2. **A mapper is a partial class marked `[Mapper]` in `AzureBank.Api.Mappers`, whose partial methods
   Mapperly implements, and a property that needs a rule gets an attribute (`[MapProperty]`)**,
   because that is all the configuration the generator needs. There are three: `AccountMapper`
   (`Account` to `AccountResponse`), `TransactionMapper` (`Transaction` to `TransactionResponse`)
   and `UserMapper` (`ApplicationUser` to `UserResponse`).

## Rejected

- Rejected: AutoMapper, because it maps by reflection at runtime (about 3 to 5 times slower than
  hand-written code in the benchmarks of [Mapperly's documentation](https://mapperly.riok.app/)),
  its conventions are harder to refactor, a configuration mistake shows only at runtime, its
  profiles add configuration, and it is not compatible with Native AOT.
- Rejected: Mapster's runtime mapping (`Adapt`), because it is about 1.7 times slower than Mapperly
  in the same benchmarks, a mapping mistake shows only at runtime, the expression trees it compiles
  are only interpreted under Native AOT, and stepping into a mapping takes an extra package
  (`ExpressionDebugger`). `Mapster.Tool`, which generates the mappers as C# files, is not evaluated.
- Rejected: manual mapping, because nothing is mapped automatically: it is as fast and as safe, and
  it is the verbose, maintenance-heavy code this decision exists to avoid.

## Consequences

- A mapping runs as plain generated C#, with no reflection at runtime, and is validated when the
  solution builds. The generated code is readable in the IDE, under the `obj` folder.
- It costs a rebuild to see a mapping change, and it is less dynamic than AutoMapper: everything is
  decided at compile time.
- Mapperly needs C# 9 and .NET 5 or later, which .NET 10 meets.
- Not covered: Mapperly's community is smaller than AutoMapper's.

## Verified by

- `dotnet build`: a mapping Mapperly cannot generate fails the build, and an unmapped property is
  reported as a warning.

## Related

ADR-0007.
