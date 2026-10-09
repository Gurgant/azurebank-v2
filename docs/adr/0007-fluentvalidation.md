# ADR-0007: FluentValidation for Request Validation

**Status:** Accepted · **Date:** 2026-01-11 · **Amended:** 2026-08-04 ·
**Decision Makers:** Vladislav Aleshaev

## Context

Request validation serves security (malformed input is refused; SQL injection is prevented by
EF Core's parameterised queries, not by validation), data integrity (only valid data
reaches the database), user experience (clear error messages) and business rules (domain
constraints). The rules must be readable when they are complex, testable in isolation, kept apart
from the DTOs, and able to express conditions and comparisons between two properties of a request.

## Decision

1. **Request rules are written as FluentValidation validators**, one `AbstractValidator<T>` per
   request under `AzureBank.Api/Validators` (`Auth`, `Account`, `Transaction`, `Transfer`),
   because fluent rules stay readable at any complexity, express conditional (`When`, `Unless`)
   and cross-property rules, and are tested by calling the validator directly.
2. **Validators are registered in DI from the API's assembly** with
   `AddValidatorsFromAssemblyContaining` (`FluentValidation.DependencyInjectionExtensions`).
3. **Controllers invoke `ValidateAndThrowAsync` by hand**, because automatic validation lives in
   `FluentValidation.AspNetCore`, which is deprecated and deliberately not referenced.
4. **`ValidationExceptionHandler` answers a failed validator with a 400 problem document**: title
   `Validation Failed`, type `https://httpstatuses.com/400`, `detail`, `instance`, `traceId`, and
   `errors` keyed by property name in camelCase (`ToCamelCase(PropertyName)`), so a consumer looks
   for `email`, not `Email`.

## Rejected

- Rejected: Data Annotations alone, because attributes express simple rules only: no conditional
  rule, limited cross-property rules ("password confirmation must match", "start date before end
  date"), and a test needs a model state.
- Rejected: automatic validation through `FluentValidation.AspNetCore`, because it is deprecated.
- Also considered: hand-written validation code, and MiniValidation (lightweight, by reflection).

## Consequences

- Rules that annotations cannot express live in a validator: a transfer to the same account, the
  scale of an amount. Each validator is unit-tested alone.
- It costs one NuGet dependency, and validators must be registered in DI.
- The DTOs carry DataAnnotations as well, so validation has two layers, and the annotations
  usually win: `[ApiController]` validates model state before the action body, so when an
  annotation fails the framework answers and `ValidateAndThrowAsync` never runs. The two answers
  differ in every field a client might branch on:

  | | model state (layer 1) | FluentValidation (layer 2) |
  |---|---|---|
  | `title` | `One or more validation errors occurred.` | `Validation Failed` |
  | `type` | `…rfc9110#section-15.5.1` | `https://httpstatuses.com/400` |
  | `detail` / `instance` | absent | both present |
  | key | the binding name: PascalCase for a DTO property (`Name`, `FromDate`), camelCase for an action parameter (`at`), a JSON path (`$.type`) when deserialisation failed | always camelCase |

- Most validators restate their annotations, so layer 2 is reached only where a validator is
  stricter: `CreateAccountRequest.Type` carries no annotation; `[MoneyRange]` checks magnitude and
  not scale, so `.ValidMoneyScale()` still bites; "transfer to the same account" is cross-field.
- Layer 2 on the wire: `POST /api/accounts {"name":"Valid Name","type":"99"}` answers
  `"errors": {"type": ["Invalid account type. Must be Checking, Savings, or Investment."]}`.
- `frontend/src/api/validationErrors.ts` carries the same table, with the raw responses.

## Verified by

- `LoginRequestValidatorTests`, `InternalTransferRequestValidatorTests` and their siblings in
  `backend/tests/AzureBank.Tests/Unit/Validators`: each validator alone.
- `frontend/src/contract/validation.contract.test.ts`: both envelopes, against the real API.

## Related

ADR-0006.
