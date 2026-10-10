# ADR-0005: Scalar API Documentation

**Status:** Accepted · **Date:** 2026-01-10 · **Decision Makers:** Vladislav Aleshaev

## Context

The REST API needs interactive documentation: to onboard a developer, to explore and try an
endpoint, to support a client integration, and as the readable form of the contract. The tool is
chosen for a modern interface, fast loading and rendering, theming, active maintenance, and
try-it-out with authentication and code samples.

## Decision

1. **The API's documentation page is Scalar (`Scalar.AspNetCore`)**, because it renders large
   schemas fast, generates code samples in more than 20 languages, has full-text search, keyboard
   navigation and a native dark mode, and integrates with .NET as a NuGet package.
2. **The page and the OpenAPI document are mapped in the Development environment only**, for
   security: a deployed API serves neither. The page is at `https://localhost:7215/scalar/v1`,
   titled "AzureBank API", and its default code sample is C# with `HttpClient`.
3. **Scalar renders the document that `Microsoft.AspNetCore.OpenApi` generates, completed by the
   API's own transformers** (the bearer scheme, the validation and error responses, the operation
   metadata), so that the page documents the authentication flow and the error bodies.

## Rejected

- Rejected: Swagger UI, because its interface is dated, its code samples are limited, its search is
  basic, it has no dark mode and its development is slow.
- Rejected: ReDoc, because it has no try-it-out and no code samples, and its .NET integration is
  manual.
- Rejected: RapiDoc, because its .NET integration is manual and it is only moderately maintained.
- Considered and not compared feature by feature: Stoplight Elements.

## Consequences

- An endpoint can be read and copied as client code from one page. Trying one from the page also
  needs the service credential (ADR-0055): in Development the page and the document are exempt
  from it, the operations they describe are not.
- It costs familiarity: Scalar is newer than Swagger UI, has a smaller community, and some advanced
  customisation requires configuration.
- Not covered: outside Development there is no documentation page.

## Verified by

- `ServiceCredentialTests.TheApiDocument_IsNotExempt_OutsideDevelopment`: outside Development a
  request for `/openapi/v1.json` that carries no service credential is refused.

## Related

ADR-0001, ADR-0055.
