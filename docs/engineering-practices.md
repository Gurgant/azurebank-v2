# Engineering practices

How this project is built and kept correct. It is deliberately **not** called `CONTRIBUTING.md`:
this is a solo portfolio project with no outside contributors, and a document that opens with "fork
and clone" is describing an audience that does not exist. The real readers are the person who wrote
this and whoever picks it up in six months.

Decisions live in [`adr/`](adr/README.md). Sharp edges that fail silently live in
[`engineering-traps.md`](engineering-traps.md). Frontend rules live in
[`../frontend/CONVENTIONS.md`](../frontend/CONVENTIONS.md).

---

## Local setup

Configuration comes from **user-secrets**, never from a committed settings file. The API fails at
startup without them, by design — `ValidateOnStart` refuses to run a bank with a missing pepper.

The API and the seeder have **separate secret stores** (different `UserSecretsId`), so every value
has to be set twice — `--project` is not optional here:

```bash
API=backend/src/AzureBank.Api
SEEDER=backend/tools/AzureBank.Seeder
CONN='Server=(localdb)\MSSQLLocalDB;Database=AzureBankDev;Trusted_Connection=True;TrustServerCertificate=True'
PEPPER='<32+ chars>'   # the SAME value goes to both projects

dotnet user-secrets --project $API    set "Jwt:Secret" "<64+ chars>"
dotnet user-secrets --project $API    set "Idempotency:HashKey" "<32+ chars>"
dotnet user-secrets --project $API    set "StepUp:BindingKey" "<32+ chars>"
dotnet user-secrets --project $API    set "Security:PinPepper" "$PEPPER"
dotnet user-secrets --project $API    set "ConnectionStrings:DefaultConnection" "$CONN"

dotnet user-secrets --project $SEEDER set "Security:PinPepper" "$PEPPER"
dotnet user-secrets --project $SEEDER set "ConnectionStrings:DefaultConnection" "$CONN"
```

If the two peppers differ, seeding succeeds and every seeded PIN then fails verification — the
failure surfaces at login, far from its cause.

**Database — one command, not `dotnet ef`:**

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project backend/tools/AzureBank.Seeder -- reset --confirm
```

That drops, migrates and seeds in one step. The environment variable is required and the `DOTNET_`
prefix is not interchangeable with `ASPNETCORE_` — see the traps document for why. Seeding also
creates the Identity roles, without which registration returns a 500.

**Running it** — start these sequentially the first time, or two parallel first builds race on
`AzureBank.Shared.dll`:

```bash
dotnet run --project backend/src/AzureBank.Api --launch-profile https   # https://localhost:7215
dotnet run --project backend/src/AzureBank.Bff --launch-profile http    # http://localhost:5000
cd frontend && npm run dev                                              # http://localhost:5173
```

The API must run the **https** profile: the BFF's proxy cluster points at 7215, so the http profile
produces a BFF that starts and then fails every proxied call.

## Quality gates

Run all of these before opening a pull request.

```bash
dotnet build
dotnet test AzureBank.slnx
dotnet format --verify-no-changes
```

**Name the solution.** A bare `dotnet test` or a filtered run both under-report: see the traps
document for the two distinct ways `--filter` silently drops the BFF suite.

Frontend changes additionally need, from `frontend/`:

```bash
npm run format:check && npm run lint && npm run build && npx vitest run
```

The type gate is `npm run build` (`tsc -b`), **not** `tsc --noEmit` — the root tsconfig is
solution-style, so `--noEmit` skips project references and misses errors the build catches.

**A gate is a delta against `main`, never an absolute count.** "This change introduces no new
alert" survives an untriaged backlog; "zero open alerts" dies at the first one and then gets
ignored — which is how eight security alerts once accumulated while a note still recorded zero.

**Never dismiss a finding to make a number look good.** A dismissal needs a reason that still
convinces on re-reading in six months. If you cannot write one, it is not a false positive, and
weakening the check to pass it defeats the only thing the check was for.

**Never blind-apply an automated fix.** Review-bot suggestions are frequently right and
occasionally confidently wrong. Verify each against the current code. The same applies in reverse:
when a bot challenges something you wrote, check the primary source, then concede or push back on
evidence rather than on conviction.

**No green without runnable proof.** "Should work" is not a test result. If a change is observable
in a browser, verify it in a browser and keep the evidence.

## Merging

**Merging is a human act.** The repository requires a pull request, permits squash only, and has no
bypass. Automated contributors open pull requests and stop there.

**Branches are not deleted after merge.** Several are cited as evidence in decision records — one
holds the only pinned reproduction of a library incompatibility — and deleting one turns that
citation into a dead end.

**Contract changes ship as two pull requests.** The endpoint, the regenerated OpenAPI spec and the
generated types land together in the backend change; the UI that consumes them follows. One pull
request spanning both makes the contract diff unreviewable.

**Integration is verified against the real stack before a release.** A green mock-mode run proves
the client's model of the protocol, never that the server agrees with it.

## Commit and pull-request titles

This repository squash-merges, so **only the pull-request title lands on `main`**. Individual
commits on the branch are discarded — commit however you like while working; the title is the one
thing that has to be right.

Plain, imperative, at most 72 characters, naming the concrete outcome rather than a category:

```text
Reject negative balances on concurrent withdrawals
Restrict workflow token permissions and sanitize the logged HTTP method
```

**Delete the `(#NN)` that GitHub pre-fills into the squash subject.** The convention here is a bare
title; the log shows one commit carrying a number and it is an anomaly, not the pattern.

**No `type(scope):` prefix.** Conventional Commits is deliberately not used: its payoff is an
automated changelog and version bump, this repository runs neither, and a consistent plain log reads
better than a half-applied taxonomy.

## Code style

Follow the [Microsoft C# conventions](https://learn.microsoft.com/dotnet/csharp/fundamentals/coding-style/coding-conventions).
Beyond those:

| Element | Convention | Example |
|---|---|---|
| Classes, methods | PascalCase | `AccountService`, `GetAccountsAsync` |
| Interfaces | `I` + PascalCase | `IAccountService` |
| Locals, parameters | camelCase | `accountBalance` |
| Private fields | `_camelCase` | `_repository` |

Async methods carry the `Async` suffix. `async void` appears only in event handlers. Nullable
reference types are on; prefer returning `null` explicitly over an empty sentinel, and handle it at
the call site. Throw the typed exceptions from `AzureBank.Shared.Exceptions` with a message that
names the thing that was missing — never a bare `Exception`.

Comments explain constraints the code cannot show. A comment that restates the next line, records
where the code came from, or argues that the change is correct is noise the moment the pull request
merges.

Feature code is organised by role: `Controllers/`, `Services/{Interfaces,Implementations}/`,
`Validators/`, `Mappers/`.

## Tests

```text
tests/AzureBank.Tests/
├── Unit/           # validators, services, utilities
├── Integration/    # API endpoints against a real host
└── Architecture/   # layer dependency and naming rules
```

Arrange–act–assert, one behaviour per test, and a name that states the behaviour rather than the
method under test. New public API surface gets tests; anything touching money gets integration
coverage, because the unit level cannot observe a transaction boundary.

**Tests that pin a decision are load-bearing.** Several ADRs name the tests that hold them, and
deleting one is a reversal of that decision rather than a cleanup. If a pinned test is in your way,
the decision is what needs revisiting.
