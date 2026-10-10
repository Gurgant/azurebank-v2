# AzureBank.Shared

The types every backend project compiles against: the entities, the API's request and response
bodies, the exceptions that become its error answers, the constants, the configuration classes and
a few helpers. The API, the BFF, Infrastructure, the notice-relay Function, the Seeder and the
AuditVerifier reference it. It holds no database mapping (that is
[AzureBank.Infrastructure](../AzureBank.Infrastructure/README.md)) and depends on no host.

It has two package references: `Microsoft.Extensions.Identity.Stores`, for the `IdentityUser<Guid>`
that `ApplicationUser` extends, and `Konscious.Security.Cryptography.Argon2`, for the PIN hash.

## What it holds

| Folder | Holds |
| --- | --- |
| `Entities/` | The persisted types. Their tables are listed in [Infrastructure](../AzureBank.Infrastructure/README.md), which maps them |
| `DTOs/` | The API's request and response bodies, by area: `Auth`, `Account`, `Transaction`, `Transfer`, `User`, and the envelopes in `Common` |
| `Exceptions/` | `AppException` and its subclasses: each carries the HTTP status and the error code of a refusal |
| `Constants/` | `ErrorCodes`, `SecurityEvents`, `ValidationRules`, `Roles`, and the header names of the idempotency and step-up protocols |
| `Enums/` | The enums the database stores, each by its member's name |
| `Options/` | The classes the configuration sections bind to, each naming its section in `SectionName` |
| `Validation/` | Attributes for a money amount, a PIN, a password, an AzureTag, a GUID that is not empty and a list with no null item |
| `Utilities/` | `IdGenerator` (account and transaction numbers), `LogSanitizer`, `SecretPrefix` |
| `Services/` | `IPasswordHasher`: Argon2id for the PIN, and for nothing else |
| `Observability/` | The console log format and the rule for an OTLP endpoint, shared by the API and the BFF |

A success body is one of the envelopes in `DTOs/Common`: `ApiResponse<T>` (`data` and `message`),
`ApiResponse` (`message` alone) where there is nothing to return, and `PaginatedResponse<T>`
(`data` and `pagination`) for the transaction list.

## Exceptions and error codes

`AppException` is abstract and carries an HTTP status, an `ErrorCode` and optional `Details`. The
API's `AppExceptionHandler` writes it as problem details: `errorCode` and `traceId` are members of
the body, so is every entry of `Details`, and a `retryAfterSeconds` detail is also sent as the
`Retry-After` header. Throw the subclass that owns the status and the code:

| Exception | Status | Code |
| --- | --- | --- |
| `AuthenticationException` | 401 | `INVALID_CREDENTIALS`, or the one passed |
| `AuthorizationException` | 403 | `ACCESS_DENIED` |
| `RegistrationClosedException` | 403 | `REGISTRATION_CLOSED` |
| `NotFoundException` | 404 | `ACCOUNT_NOT_FOUND`, whatever the resource: the frontend mirrors it, and `NotFoundExceptionTests` pins it |
| `ConflictException` | 409 | `CONFLICT`, or the one passed |
| `IdempotencyException` | 400, 409, 413 or 422 | One of the `IDEMPOTENCY_*` codes (ADR-0009) |
| `PayloadTooLargeException` | 413 | `PAYLOAD_TOO_LARGE` |
| `BusinessRuleException` | 422 | `BUSINESS_RULE_VIOLATION`, or the one passed |
| `InsufficientFundsException` | 422 | `INSUFFICIENT_FUNDS`, with `available` and `requested` |
| `DailyLimitExceededException` | 422 | `DAILY_LIMIT_EXCEEDED`, with `limit`, `used`, `requested` and `resetsAt` |
| `AccountLockedException`, `PinLockedException` | 429 | `ACCOUNT_LOCKED`, `PIN_LOCKED`, each with `retryAfterSeconds` and `lockedUntil` |
| `DemoRefusalException` | 429 | `DEMO_POOL_EMPTY`, `DEMO_DAILY_LIMIT` (with `retryAfterSeconds`) or `DEMO_COPY_LIMIT` |
| `ServiceUnavailableException` | 503 | `SERVICE_UNAVAILABLE`, with `retryAfterSeconds` |

The sentence of a money refusal carries no figure: the amounts travel in `Details`, and the client
formats them.

An error code is written once, in `ErrorCodes`: `ErrorCodeConstantTests` fails a code written as a
string literal in the API's services or in `Exceptions/`. A security event's name is written once,
in `SecurityEvents`, which is here because both the API and the BFF log them:
`SecurityEventConstantTests` fails a name written inline at a log site.

## Limits

`Constants/ValidationRules.cs` is where a limit is written once. The attributes on the DTOs, the
API's validators and the column widths in Infrastructure read it, and the API publishes the same
values in the OpenAPI document.

| Rule | Value |
| --- | --- |
| Currency | `EUR` |
| One amount | 0.01 to 100,000.00, with at most two decimal places; stored as `decimal(19,4)` |
| Password | 8 to 128 printable ASCII characters, with a lowercase letter, an uppercase letter, a digit and a special character |
| PIN | Exactly six digits, `0` to `9`. Three wrong attempts in a row lock it for 15 minutes |
| Sign-in | Five failed attempts in a row lock the account for 15 minutes |
| AzureTag | 3 to 20 characters: a lowercase letter, then lowercase letters, digits and underscores |
| Names and text | First and last name 2 to 50 characters, account name 2 to 100, e-mail at most 255, description at most 500 |
| Account number | `AB-XXXX-XXXX-XX`, all digits after the prefix |
| Transaction number | `TXN-YYYYMMDD-XXXXXXXXXXC`: 24 characters, the last a check symbol |
| Page size | 1 to 100, and 20 when none is asked for |

Money leaves the server as a number. Where the server itself has to state a figure in a sentence
it calls `ValidationRules.DescribeAmount`: an invariant number and the ISO code, never a symbol and
never the process's culture. `MoneyFormattingTests` fails a `C` format and a currency symbol in a
message.

## Test it

From `backend/`:

```bash
dotnet test tests/AzureBank.Tests/AzureBank.Tests.csproj
```

Its tests are under `tests/AzureBank.Tests/Unit`, by folder: `Utilities`, `Exceptions`, `Options`,
`Validators`, `Observability`, and `Services/PasswordHasher*Tests.cs`. The three guards named
above are in `tests/AzureBank.Tests/Architecture`; `CommittedOpenApiDocumentTests`, named below, is
in `tests/AzureBank.Tests/Integration`.

## Easy to get wrong

- **A DTO is the contract.** A change to a request or response type, to its attributes or to a
  `ValidationRules` value they use changes `docs/api/openapiv1.json`, and
  `CommittedOpenApiDocumentTests` fails until the committed file is what the API generates
  (ADR-0053). How to regenerate it: [docs/api/README.md](../../../docs/api/README.md).
- **An entity change is a schema change, and so is a width in `ValidationRules`**: both need a
  migration ([Infrastructure](../AzureBank.Infrastructure/README.md)).
- **An enum member's name is stored in the database.** Reordering members changes nothing;
  renaming one changes what the stored rows say.
- **`IPasswordHasher` hashes the PIN and nothing else**: Argon2id, keyed with the pepper in
  `Security:PinPepper` (ADR-0003, ADR-0011). Account passwords are hashed by ASP.NET Core Identity
  and never reach it.
- **`LogSanitizer.Sanitize(string)` is named in a CodeQL model**,
  `.github/codeql/extensions/azurebank-csharp-models/models/logsanitizer.model.yml`, by its
  namespace, type, name and signature. Rename it, move it or add an overload and that row matches
  nothing, with no error: change the file in the same commit (ADR-0017, decisions 6 and 7). Its
  result is safe in a log line, and only there.
- **A secret reaches a log line only through `SecretPrefix.Of`**: its first eight characters
  (ADR-0017, the log-identifier rule).
- **`AzureTag` is a public handle that can be renamed, not a login.** Identity's `UserName` holds
  the user's id, and sign-in is by e-mail (ADR-0015).
- **A tracked `Transaction` cannot be changed or deleted**: the context refuses the save
  ([what every save does](../AzureBank.Infrastructure/README.md#what-every-save-does)).
- **`IdGenerator.IsValidTransactionNumber` answers false for the rows written before the check
  symbol**, which keep their shorter numbers of 19 and 20 characters. Nothing on the request path
  calls it.

## See also

- [backend/README.md](../../README.md): the solution, and how to run it.
- [AzureBank.Infrastructure](../AzureBank.Infrastructure/README.md): the tables, the context and
  the migrations.
- [docs/adr](../../../docs/adr/README.md): the decision records cited here by number.
