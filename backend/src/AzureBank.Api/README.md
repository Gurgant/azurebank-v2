# AzureBank.Api

The REST API of AzureBank: sign-in and tokens, accounts, deposits, withdrawals and transfers, with
the business rules behind them. It serves one client, the BFF (ADR-0055): a browser reaches it
only through the BFF's proxy, and it has no CORS (ADR-0018). Part of the
[backend solution](../../README.md).

## What is here

The main folders:

| Folder | What it holds |
|---|---|
| `Controllers/` | Five controllers: auth, accounts, transactions, transfers, users |
| `Services/` | The business logic behind interfaces, and the background services: two clean-up sweeps, the notice relay, the start-up check of the audit key ring |
| `Middleware/`, `Attributes/` | The pipeline in front of the controllers, and the markers on an endpoint that it reads, such as `[TokenEndpoint]` and `[RequireIdempotency]` |
| `Validators/`, `Handlers/` | The FluentValidation validators; the exception handlers |
| `Transformers/` | What shapes the generated OpenAPI document |
| `Extensions/`, `Program.cs` | The registrations, grouped by concern, and the pipeline in its order |

It references [`AzureBank.Shared`](../AzureBank.Shared/README.md) (entities, DTOs, exceptions) and
[`AzureBank.Infrastructure`](../AzureBank.Infrastructure/README.md) (the `DbContext` and the
migrations).

## Run it

From `backend/`, once the [local setup](../../../docs/engineering-practices.md#local-setup) has
set the secrets and created the database:

```bash
dotnet run --project src/AzureBank.Api --launch-profile https
```

It listens on `https://localhost:7215` and `http://localhost:5068`. The BFF's proxy points at the
first, and the `http` profile opens only the second. In Development, and in no other environment,
it serves its documentation at <https://localhost:7215/scalar/v1> and the generated contract at
`/openapi/v1.json`. `/health/live` answers while the process is up; `/health/ready` runs the
`database` and `audit-chain` checks and names each with its status.

## In front of every endpoint

After the request log and the exception handler, a request passes these, in this order. The order
is part of the behaviour: it decides which refusal a caller gets, and whether a refused request
leaves a record. `Program.cs` says why each step sits where it does.

1. **The request deadline** (ADR-0058). A request still running after `RequestDeadline:Seconds`
   is cancelled and answers 503: [the limits](#database-limits-and-the-request-deadline).
2. **The service key** (ADR-0055). Without exactly one `X-AzureBank-Service-Key` header holding
   `ServiceCredential:BffKey`, the answer is 401 `SERVICE_CREDENTIAL_REQUIRED`, before the token
   is looked at. `/health/*` is exempt, and in Development `/openapi` and `/scalar`: the
   documentation opens in a browser, and a call made by hand needs the header.
3. **The token road** (ADR-0057 §4.2). The six token endpoints (login, register, refresh, revoke,
   logout and the demo's claim) and the stamp feed, session-stamps, answer only the BFF's own
   client: a request from loopback carrying exactly one `X-AzureBank-Token-Road` header. Anything
   else gets 404, as an unknown path would.
4. **The demo's flag** (ADR-0063). While `Demo:Enabled` is false the claim answers 404; while it
   is true registration answers 403 `REGISTRATION_CLOSED`, whatever the body.
5. **Authentication**: the JWT bearer.
6. **The demo's count of changes** (ADR-0063). On the public demo each request of a signed-in
   user that could change something, and each reveal of an account number, is counted on that
   user's copy; the one past `Demo:Copy:MaxWrites` answers 429 `DEMO_COPY_LIMIT`. A token
   endpoint is never counted: of the six, only signing out everywhere asks for a signed-in user,
   and it is answered the same whatever the copy has spent.
7. **Idempotency** (ADR-0009): the four money endpoints need an `Idempotency-Key` header.

On the public demo sign-in lets in only the owner of a claimed demo copy whose time is not over,
and answers everybody else as an email nobody has (ADR-0063).

## Endpoints

The contract is [`docs/api/openapiv1.json`](../../../docs/api/README.md): this project generates
it, and a test compares the committed file with what it generates (ADR-0053). "Bearer" is the
access token, which the BFF adds.

| Endpoint | What it does | Needs |
|---|---|---|
| `POST /api/auth/login` | Sign in: the tokens and the user | Token road |
| `POST /api/auth/register` | Register a user with a first account; on the public demo 403 `REGISTRATION_CLOSED` | Token road |
| `POST /api/auth/demo/claim` | On the public demo only: take a free demo copy for a visitor, give its owner a password and sign in as that owner; answers the tokens, the user and what signs in to the copy again | Token road; the BFF names the visitor's address |
| `POST /api/auth/refresh` | Renew the access token with the session's grant; the grant is not rotated | Token road; the grant is the credential |
| `POST /api/auth/revoke` | Revoke the grants of ended sessions; 200 for unknown grants too | Token road; the grant is the credential |
| `POST /api/auth/logout` | Revoke every grant of the user (every session) and raise the user's session stamp | Token road, bearer |
| `POST /api/auth/session-stamps` | Read the session stamps of the listed users (the BFF's watcher) | Token road |
| `GET /api/auth/me` | The current user | Bearer |
| `POST /api/auth/pin` | Set the PIN; changing it takes `currentPin` (ADR-0040) | Bearer |
| `POST /api/auth/pin/verify` | Check a PIN: 200 with `verified` true or false | Bearer |
| `GET /api/accounts`, `POST /api/accounts` | List the user's accounts; create one | Bearer |
| `GET /api/accounts/{id}`, `PATCH /api/accounts/{id}` | Read an account; change its name | Bearer |
| `PATCH /api/accounts/{id}/set-primary` | Make it the primary account | Bearer |
| `GET /api/accounts/{id}/balance` | The balance, current or historical | Bearer |
| `GET /api/accounts/{id}/full-number` | Reveal the full account number, answered `no-store`; the PIN in front of this path is the BFF's to ask for (ADR-0020) | Bearer |
| `POST /api/accounts/{id}/deletion-authorizations` | Mint a one-shot authorisation to close the account (ADR-0049); the balance and primary rules answer 422 before the PIN is consulted | Bearer, the PIN in the body |
| `DELETE /api/accounts/{id}` | Close the account, a soft delete | Bearer, `Step-Up-Authorization` |
| `GET /api/transactions`, `GET /api/transactions/{id}` | The history, filtered and paged; one transaction | Bearer |
| `GET /api/transactions/summary` | Income, expenses, net and pending count over a date window, the current UTC month by default | Bearer |
| `POST /api/transactions/deposit` | Deposit | Bearer, `Idempotency-Key` |
| `POST /api/transactions/withdraw/authorizations` | Mint a one-shot authorisation for a withdrawal (ADR-0056) | Bearer, the PIN in the body |
| `POST /api/transactions/withdraw` | Withdraw | Bearer, `Idempotency-Key`, `Step-Up-Authorization` |
| `POST /api/transfers/authorizations`, `POST /api/transfers/internal/authorizations` | Mint a one-shot authorisation for a transfer, to another user or between own accounts (ADR-0042) | Bearer, the PIN in the body |
| `POST /api/transfers`, `POST /api/transfers/internal` | Transfer to another user; between own accounts | Bearer, `Idempotency-Key`, `Step-Up-Authorization` |
| `GET /api/users/{azureTag}` | Look a recipient up by exact AzureTag: 200 with `exists` true or false, never 404 (ADR-0014) | Bearer |
| `PATCH /api/users/me/azuretag` | Rename the user's own AzureTag (ADR-0015) | Bearer |

A mint answers 201 with `authorizationId` and `expiresAt`, and the operation it authorises
presents that id in `Step-Up-Authorization`. That header's refusals are 401s:
`AUTHORIZATION_REQUIRED` with no header, `AUTHORIZATION_INVALID` for an authorisation already
spent, minted for something else or not the caller's own, and `AUTHORIZATION_EXPIRED` past its
window, which is `StepUp:Window`, two minutes.

## Validation

A request passes two layers, and it matters which one answers (ADR-0007).

1. **DataAnnotations on the DTO**, run by `[ApiController]` before the action body. A failure is
   answered at once, with the title `"One or more validation errors occurred."` and keyed by the
   binding name, and the layer below never runs.
2. **FluentValidation**, called by hand in the action (`ValidateAndThrowAsync`).
   `ValidationExceptionHandler` turns the throw into the title `"Validation Failed"` with
   camelCased keys. The validators are registered through DI
   (`AddValidatorsFromAssemblyContaining<Program>`); auto-validation is not wired, because
   `FluentValidation.AspNetCore` is deprecated.

So layer 2 answers only where a validator is stricter than every annotation on that property:
the unannotated `CreateAccountRequest.Type`, the money scale, which `[MoneyRange]` does not
check, and the same-account rule of an internal transfer. A request DTO with no validator, such as
`RefreshRequest`, `RevokeRequest` or `UpdateAzureTagRequest`, can only produce the layer-1 answer.

The limits are constants in `../AzureBank.Shared/Constants/ValidationRules.cs`: a password of 8
to 128 characters with an uppercase letter, a lowercase letter, a digit and a special character;
a PIN of exactly 6 digits; an AzureTag of 3 to 20 characters, lowercase, starting with a letter;
an account name of 2 to 100 characters; an email of at most 255 characters; an amount from 0.01
to 100,000.00 with at most 2 decimal places.

## Configuration

`appsettings.json` holds what is not a secret, and its comments say what a value bounds and why.
An access token lives 15 minutes (`Jwt:ExpirationMinutes`), and a session's grant 60 minutes from
sign-in (`Jwt:RefreshTokenLifetimeMinutes`).

The console is not configured there: the code writes it, one JSON object per line in Production
and text everywhere else (`ConsoleLogFormat`). A `Serilog:WriteTo` in configuration takes its
place; in Production such a console should use `RenderedCompactJsonFormatter`, to match the
bootstrap lines.

### Database limits and the request deadline

How long the API waits on the database, how often it tries again, and when it gives up on a
request (ADR-0058). Each is checked at start, and the API logs the limits it opened with once it
has started (`Database limits: …`).

| Setting | Default | In `appsettings.json` | What it bounds |
|---|---|---|---|
| `Database:MaxRetryCount` | 4 | yes | EF's retries of a transient failure (0 to 20; 0 turns retrying off). A command timeout, −2, is never retried |
| `Database:MaxRetryDelay` | `00:00:10` | yes | The cap on EF's back-off between two retries (above zero, at most a minute) |
| `Database:ConnectTimeoutSeconds` | 10 | no | Opening a connection, and every BEGIN, COMMIT and ROLLBACK (1 to 60). Not an open to a name that does not resolve: on the compose stack those took 11.81 to 23.75 s (ADR-0058) |
| `Database:ConnectRetryCount` | 0 | no | SqlClient's own retries of an open (0 to 255): 0 leaves EF the only retry layer |
| `Database:MaxPoolSize` | 12 | no | Connections one process keeps open (1 to 200) |
| `RequestDeadline:Seconds` | 40 | yes | A request still running after it answers 503 `SERVICE_UNAVAILABLE` (1 to 600). Refresh, revoke and logout have none |

The three connection limits, and `Pool Blocking Period=NeverBlock`, are written into the
connection string only where the string leaves them unset (`SqlConnectionDefaults`), so a value the
string sets wins, under any of its names. A commit that has started is never cancelled by the
deadline, and none starts after it has fired.

### Environment variables

| Variable | Description |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | Runtime environment (Development/Production) |
| `ConnectionStrings__DefaultConnection` | Database connection string; it must be there, parse and name a server, checked at startup |
| `Jwt__Secret` | JWT signing key; 32+ bytes as UTF-8, checked at startup |
| `Idempotency__HashKey` | Request-fingerprint HMAC key (32+ chars, ADR-0009) |
| `StepUp__BindingKey` | Step-up binding HMAC key (32+ chars, ADR-0042) |
| `Audit__ChainKey` | Audit hash-chain HMAC key (32+ chars, ADR-0044) |
| `Audit__AnchorKey` | Audit anchor-record HMAC key (32+ chars, ADR-0044) |
| `Security__PinPepper` | PIN-hash pepper (32+ chars, ADR-0011); the Seeder needs the same value |
| `ServiceCredential__BffKey` | The key the BFF presents in `X-AzureBank-Service-Key` (32+ chars, ADR-0055); the BFF needs the same value |

The API refuses to start, with exit code 1, when the connection string or a secret above is
missing or too short. With `Demo__Enabled=true` it also needs `Demo__ClientKeySecret`, 32
characters or more. In development every value above but `ASPNETCORE_ENVIRONMENT` comes from
`dotnet user-secrets` (`:` instead of `__`): the
[local setup](../../../docs/engineering-practices.md#local-setup) is the one copy of the recipe.

## Errors

Every error is a ProblemDetails body that carries a `traceId`.

- **A domain refusal** is an `AppException` (`../AzureBank.Shared/Exceptions`), which fixes its
  status and its `errorCode`: `AuthenticationException` 401, `AuthorizationException` 403,
  `NotFoundException` 404, `ConflictException` 409, `BusinessRuleException` and
  `InsufficientFundsException` 422, a locked sign-in or PIN and the demo's `DEMO_*` refusals 429.
  `AppExceptionHandler` writes the body, and a `Retry-After` header when the refusal carries
  `retryAfterSeconds`.
- **A validation failure** is 400, with an `errors` dictionary and no `errorCode`.
- **An outage** is 503 `SERVICE_UNAVAILABLE` (ADR-0058): a database that cannot be reached, a
  command timeout, an exhausted pool, EF's retries spent, or a request past its deadline. It
  comes from `ServiceUnavailableExceptionHandler` with `retryAfterSeconds` 10, `Retry-After` and
  a `Cache-Control` that includes `no-store` (on the wire `no-cache,no-store`, with
  `Pragma: no-cache` and `Expires: -1`). On the four money endpoints it adds `applied: false`
  when the request is known to have changed nothing. Revoke answers 503 when its database write
  fails: send it again after `retryAfterSeconds`.
- **A client that hung up** gets no body at all.
- **Anything else unexpected** is the 500, with no `errorCode`.

## Easy to get wrong

- **An XML `<summary>` on a DTO property is published.** It becomes that property's description in
  the contract whenever no validation attribute supplies one, so reasoning goes in ordinary
  comments.
- **A change to a route or a DTO changes the contract.** `CommittedOpenApiDocumentTests` fails
  until `docs/api/openapiv1.json` is generated again:
  [the OpenAPI document](../../../docs/api/README.md) says how.
- **A new required setting is invisible to the tests.** Each secret is checked at start, and
  `CustomWebApplicationFactory` injects every one, so the suite stays green while the workflows
  and a developer's own secrets lack the new value.
  [Engineering traps](../../../docs/engineering-traps.md) lists the places to update.

## See also

- [`tests/api-collection`](../../../tests/api-collection/README.md): the Bruno collection, whose
  requests carry the service key.
- [How AzureBank works](../../../docs/architecture/overview.md) and
  [`SECURITY.md`](../../../SECURITY.md): the system, and which host enforces what.
- [AzureBank.Tests](../../tests/AzureBank.Tests/README.md): the tests that start this host.
