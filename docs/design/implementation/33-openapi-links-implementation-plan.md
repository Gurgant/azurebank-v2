# OpenAPI Links Implementation Plan

> **Document Version**: 1.0
> **Created**: 2026-01-14
> **Status**: Ready for Implementation
> **Related**: [32-schemathesis-strict-mode-implementation.md](./32-schemathesis-strict-mode-implementation.md)

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Prerequisites Analysis](#2-prerequisites-analysis)
3. [OpenAPI Links Specification](#3-openapi-links-specification)
4. [Implementation Design](#4-implementation-design)
5. [Step-by-Step Implementation](#5-step-by-step-implementation)
6. [Testing Strategy](#6-testing-strategy)
7. [Best Practices](#7-best-practices)

---

## 1. Executive Summary

### 1.1 Objective

Add OpenAPI Links to the AzureBank API specification to enable **stateful testing** with Schemathesis. Links define relationships between operations, allowing test frameworks to chain calls automatically:

```
POST /api/accounts (Create) → Extract ID → GET /api/accounts/{id} (Read)
```

### 1.2 Current Gap

| Feature | Current Status | Required |
|---------|----------------|----------|
| Operation IDs | Missing | Required for Links |
| Response Links | Missing | Target feature |
| Stateful Testing | Not available | Goal |

### 1.3 Solution Overview

Implement two new transformers:

1. **OperationIdTransformer** - Add unique operationId to each operation
2. **OpenApiLinksTransformer** - Add links to responses that create/return resources

---

## 2. Prerequisites Analysis

### 2.1 Missing: operationId

OpenAPI Links require either `operationId` or `operationRef` to identify the target operation. Currently, the AzureBank OpenAPI spec has **no operationId** fields.

**Before** (current):
```json
"/api/accounts/{id}": {
  "get": {
    "tags": ["Account"],
    "summary": "Get a specific account by ID."
    // NO operationId
  }
}
```

**After** (required):
```json
"/api/accounts/{id}": {
  "get": {
    "operationId": "GetAccountById",
    "tags": ["Account"],
    "summary": "Get a specific account by ID."
  }
}
```

### 2.2 Response Structure Analysis

The API uses wrapped responses with `ApiResponse<T>` pattern:

```json
{
  "data": {
    "id": "guid-here",
    "name": "Account Name",
    ...
  },
  "message": "Success"
}
```

**Runtime Expression Path**: `$response.body#/data/id`

---

## 3. OpenAPI Links Specification

### 3.1 Link Object Structure

From [OpenAPI 3.1 Specification](https://spec.openapis.org/oas/v3.1.0.html) and [Swagger Docs](https://swagger.io/docs/specification/v3_0/links/):

```yaml
links:
  LinkName:
    operationId: targetOperationId
    parameters:
      paramName: '$response.body#/path/to/value'
    description: Optional description
```

### 3.2 Runtime Expressions

| Expression | Description |
|------------|-------------|
| `$response.body#/data/id` | Extract `id` from response data |
| `$response.body#/data/accountId` | Extract account ID from data |
| `$request.path.id` | Use path parameter from request |

### 3.3 AzureBank Links Design

| Source Operation | Response Code | Link Name | Target Operation | Parameter Mapping |
|------------------|---------------|-----------|------------------|-------------------|
| POST /api/accounts | 201 | GetAccount | GET /api/accounts/{id} | `id: $response.body#/data/id` |
| POST /api/accounts | 201 | UpdateAccount | PATCH /api/accounts/{id} | `id: $response.body#/data/id` |
| POST /api/accounts | 201 | DeleteAccount | DELETE /api/accounts/{id} | `id: $response.body#/data/id` |
| POST /api/accounts | 201 | GetBalance | GET /api/accounts/{id}/balance | `id: $response.body#/data/id` |
| POST /api/transactions/deposit | 201 | GetTransaction | GET /api/transactions/{id} | `id: $response.body#/data/id` |
| POST /api/transactions/withdraw | 201 | GetTransaction | GET /api/transactions/{id} | `id: $response.body#/data/id` |
| POST /api/auth/register | 201 | GetCurrentUser | GET /api/auth/me | (no params) |

---

## 4. Implementation Design

### 4.1 Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    TRANSFORMER PIPELINE                          │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Order 1: OperationIdTransformer (Document Transformer)          │
│           ├─ Iterate all paths and operations                   │
│           └─ Generate unique operationId for each               │
│                                                                  │
│  Order 2: OpenApiLinksTransformer (Document Transformer)         │
│           ├─ Find operations that create resources               │
│           └─ Add links to their success responses               │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

### 4.2 Operation ID Naming Convention

| Path | Method | OperationId |
|------|--------|-------------|
| /api/accounts | GET | GetAccounts |
| /api/accounts | POST | CreateAccount |
| /api/accounts/{id} | GET | GetAccountById |
| /api/accounts/{id} | PATCH | UpdateAccount |
| /api/accounts/{id} | DELETE | DeleteAccount |
| /api/accounts/{id}/balance | GET | GetAccountBalance |
| /api/accounts/{id}/set-primary | PATCH | SetPrimaryAccount |
| /api/transactions | GET | GetTransactions |
| /api/transactions/{id} | GET | GetTransactionById |
| /api/transactions/deposit | POST | CreateDeposit |
| /api/transactions/withdraw | POST | CreateWithdrawal |
| /api/transfers | POST | CreateTransfer |
| /api/transfers/internal | POST | CreateInternalTransfer |
| /api/auth/register | POST | RegisterUser |
| /api/auth/login | POST | LoginUser |
| /api/auth/me | GET | GetCurrentUser |
| /api/auth/logout | POST | LogoutUser |
| /api/auth/pin | POST | SetPin |
| /api/auth/pin/verify | POST | VerifyPin |
| /api/users/search | GET | SearchUsers |
| /api/users/{azureTag} | GET | GetUserByAzureTag |

### 4.3 File Structure

```
src/AzureBank.Api/Transformers/
├── OperationIdTransformer.cs      (NEW)
├── OpenApiLinksTransformer.cs     (NEW)
├── BusinessRulesDocumentTransformer.cs
├── NotFoundResponseTransformer.cs
└── ... (other existing transformers)
```

---

## 5. Step-by-Step Implementation

### Step 1: Create OperationIdTransformer.cs

**Purpose**: Add unique `operationId` to each operation

**Location**: `src/AzureBank.Api/Transformers/OperationIdTransformer.cs`

```csharp
using Microsoft.AspNetCore.OpenApi;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI Document Transformer that adds unique operationId to each operation.
///
/// operationId is REQUIRED for OpenAPI Links to work. Each operation needs a
/// unique identifier that links can reference via the operationId field.
///
/// Naming Convention:
/// - GET collection: Get{Resource}s (e.g., GetAccounts)
/// - GET single: Get{Resource}ById (e.g., GetAccountById)
/// - POST create: Create{Resource} (e.g., CreateAccount)
/// - PATCH update: Update{Resource} (e.g., UpdateAccount)
/// - DELETE: Delete{Resource} (e.g., DeleteAccount)
/// </summary>
public sealed class OperationIdTransformer : IOpenApiDocumentTransformer
{
    /// <summary>
    /// Mapping of "METHOD /path" to operationId.
    /// </summary>
    private static readonly Dictionary<string, string> OperationIds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Account operations
        ["GET /api/accounts"] = "GetAccounts",
        ["POST /api/accounts"] = "CreateAccount",
        ["GET /api/accounts/{id}"] = "GetAccountById",
        ["PATCH /api/accounts/{id}"] = "UpdateAccount",
        ["DELETE /api/accounts/{id}"] = "DeleteAccount",
        ["GET /api/accounts/{id}/balance"] = "GetAccountBalance",
        ["PATCH /api/accounts/{id}/set-primary"] = "SetPrimaryAccount",

        // Transaction operations
        ["GET /api/transactions"] = "GetTransactions",
        ["GET /api/transactions/{id}"] = "GetTransactionById",
        ["POST /api/transactions/deposit"] = "CreateDeposit",
        ["POST /api/transactions/withdraw"] = "CreateWithdrawal",

        // Transfer operations
        ["POST /api/transfers"] = "CreateTransfer",
        ["POST /api/transfers/internal"] = "CreateInternalTransfer",

        // Auth operations
        ["POST /api/auth/register"] = "RegisterUser",
        ["POST /api/auth/login"] = "LoginUser",
        ["GET /api/auth/me"] = "GetCurrentUser",
        ["POST /api/auth/logout"] = "LogoutUser",
        ["POST /api/auth/pin"] = "SetPin",
        ["POST /api/auth/pin/verify"] = "VerifyPin",

        // User operations
        ["GET /api/users/search"] = "SearchUsers",
        ["GET /api/users/{azureTag}"] = "GetUserByAzureTag"
    };

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (document.Paths == null)
            return Task.CompletedTask;

        foreach (var (path, pathItem) in document.Paths)
        {
            if (pathItem.Operations == null)
                continue;

            foreach (var (method, operation) in pathItem.Operations)
            {
                var key = $"{method.ToString().ToUpperInvariant()} {path}";

                if (OperationIds.TryGetValue(key, out var operationId))
                {
                    operation.OperationId = operationId;
                }
                else
                {
                    // Generate fallback operationId for unmapped operations
                    operation.OperationId = GenerateFallbackOperationId(method, path);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static string GenerateFallbackOperationId(OperationType method, string path)
    {
        // Convert "/api/accounts/{id}/balance" to "AccountsIdBalance"
        var sanitized = path
            .Replace("/api/", "")
            .Replace("{", "")
            .Replace("}", "")
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(ToPascalCase)
            .Aggregate("", (a, b) => a + b);

        return $"{method}{sanitized}";
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return char.ToUpperInvariant(input[0]) + input[1..].ToLowerInvariant();
    }
}
```

### Step 2: Create OpenApiLinksTransformer.cs

**Purpose**: Add links to responses that create resources

**Location**: `src/AzureBank.Api/Transformers/OpenApiLinksTransformer.cs`

```csharp
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace AzureBank.Api.Transformers;

/// <summary>
/// OpenAPI Document Transformer that adds Links to response objects.
///
/// Links enable stateful testing with Schemathesis by defining how data
/// from one operation's response can be used in subsequent operations.
///
/// Example:
/// POST /api/accounts → 201 → { data: { id: "..." } }
///                          ↓ Link extracts id
/// GET /api/accounts/{id} ← uses extracted id
///
/// Reference: https://swagger.io/docs/specification/v3_0/links/
/// </summary>
public sealed class OpenApiLinksTransformer : IOpenApiDocumentTransformer
{
    /// <summary>
    /// Link definitions for resource-creating operations.
    /// Key: "METHOD /path"
    /// Value: List of links to add to the success response
    /// </summary>
    private static readonly Dictionary<string, LinkDefinition[]> ResponseLinks = new(StringComparer.OrdinalIgnoreCase)
    {
        // POST /api/accounts → Links to account operations
        ["POST /api/accounts"] = new LinkDefinition[]
        {
            new("GetCreatedAccount", "GetAccountById", new() { ["id"] = "$response.body#/data/id" }),
            new("UpdateCreatedAccount", "UpdateAccount", new() { ["id"] = "$response.body#/data/id" }),
            new("DeleteCreatedAccount", "DeleteAccount", new() { ["id"] = "$response.body#/data/id" }),
            new("GetCreatedAccountBalance", "GetAccountBalance", new() { ["id"] = "$response.body#/data/id" }),
            new("SetCreatedAccountPrimary", "SetPrimaryAccount", new() { ["id"] = "$response.body#/data/id" })
        },

        // POST /api/transactions/deposit → Link to get transaction
        ["POST /api/transactions/deposit"] = new LinkDefinition[]
        {
            new("GetCreatedTransaction", "GetTransactionById", new() { ["id"] = "$response.body#/data/id" })
        },

        // POST /api/transactions/withdraw → Link to get transaction
        ["POST /api/transactions/withdraw"] = new LinkDefinition[]
        {
            new("GetCreatedTransaction", "GetTransactionById", new() { ["id"] = "$response.body#/data/id" })
        },

        // POST /api/transfers → Link to get transactions (both sides)
        ["POST /api/transfers"] = new LinkDefinition[]
        {
            new("GetSenderTransaction", "GetTransactionById", new() { ["id"] = "$response.body#/data/senderTransactionId" }),
            new("GetRecipientTransaction", "GetTransactionById", new() { ["id"] = "$response.body#/data/recipientTransactionId" })
        },

        // POST /api/transfers/internal → Link to get transactions
        ["POST /api/transfers/internal"] = new LinkDefinition[]
        {
            new("GetSourceTransaction", "GetTransactionById", new() { ["id"] = "$response.body#/data/sourceTransactionId" }),
            new("GetDestinationTransaction", "GetTransactionById", new() { ["id"] = "$response.body#/data/destinationTransactionId" })
        },

        // POST /api/auth/register → Link to get current user
        ["POST /api/auth/register"] = new LinkDefinition[]
        {
            new("GetRegisteredUser", "GetCurrentUser", new())
        }
    };

    /// <summary>
    /// Status codes to add links to (typically 200, 201).
    /// </summary>
    private static readonly string[] SuccessStatusCodes = ["200", "201"];

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (document.Paths == null)
            return Task.CompletedTask;

        foreach (var (path, pathItem) in document.Paths)
        {
            if (pathItem.Operations == null)
                continue;

            foreach (var (method, operation) in pathItem.Operations)
            {
                var key = $"{method.ToString().ToUpperInvariant()} {path}";

                if (ResponseLinks.TryGetValue(key, out var links))
                {
                    AddLinksToOperation(operation, links);
                }
            }
        }

        return Task.CompletedTask;
    }

    private static void AddLinksToOperation(OpenApiOperation operation, LinkDefinition[] links)
    {
        if (operation.Responses == null)
            return;

        foreach (var statusCode in SuccessStatusCodes)
        {
            if (!operation.Responses.TryGetValue(statusCode, out var response))
                continue;

            // Initialize Links dictionary if null
            response.Links ??= new Dictionary<string, OpenApiLink>();

            foreach (var linkDef in links)
            {
                if (response.Links.ContainsKey(linkDef.Name))
                    continue;

                response.Links[linkDef.Name] = new OpenApiLink
                {
                    OperationId = linkDef.TargetOperationId,
                    Parameters = linkDef.Parameters.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new RuntimeExpressionAnyWrapper(
                            RuntimeExpression.Build(kvp.Value)
                        )
                    )
                };
            }
        }
    }

    /// <summary>
    /// Definition of a link to add to a response.
    /// </summary>
    private sealed record LinkDefinition(
        string Name,
        string TargetOperationId,
        Dictionary<string, string> Parameters
    );
}
```

### Step 3: Register Transformers in ServiceCollectionExtensions.cs

**Location**: `src/AzureBank.Api/Extensions/ServiceCollectionExtensions.cs`

Add the following lines in the `AddOpenApi` configuration:

```csharp
// Document transformer: Add operationId to all operations
// MUST be registered BEFORE OpenApiLinksTransformer (links reference operationIds)
options.AddDocumentTransformer<OperationIdTransformer>();

// Document transformer: Add Links to responses for stateful testing
// Enables Schemathesis --stateful=links for CRUD workflow testing
options.AddDocumentTransformer<OpenApiLinksTransformer>();
```

### Step 4: Regenerate OpenAPI Specification

```bash
# Start the API to regenerate spec
cd C:\Dev\repos\AzureBank\backend
dotnet run --project src/AzureBank.Api

# In another terminal, fetch the new spec
curl http://localhost:5068/openapi/v1.json -o openapiv1.json
```

### Step 5: Verify Links in Specification

Check that the generated spec contains links:

```json
"/api/accounts": {
  "post": {
    "operationId": "CreateAccount",
    "responses": {
      "201": {
        "links": {
          "GetCreatedAccount": {
            "operationId": "GetAccountById",
            "parameters": {
              "id": "$response.body#/data/id"
            }
          }
        }
      }
    }
  }
}
```

### Step 6: Test with Schemathesis Stateful Mode

```bash
cd C:\Dev\repos\AzureBank\backend

# Clear Python cache
Remove-Item -Recurse -Force "__pycache__" -ErrorAction SilentlyContinue

# Run stateful testing
schemathesis run ./openapiv1.json --stateful=links --url http://localhost:5068
```

---

## 6. Testing Strategy

### 6.1 Verification Checklist

| Step | Verification | Command/Action |
|------|--------------|----------------|
| 1 | All operations have operationId | Check openapiv1.json |
| 2 | Links appear in 201 responses | Check openapiv1.json |
| 3 | Runtime expressions are valid | Validate JSON |
| 4 | Schemathesis recognizes links | Run with `--verbosity 2` |
| 5 | Stateful tests pass | Run with `--stateful=links` |

### 6.2 Expected Schemathesis Output

```
Schemathesis v4.8.0
━━━━━━━━━━━━━━━━━━━

 ✅  Loaded specification from ./openapiv1.json
 ✅  Detected 7 OpenAPI Links for stateful testing

 ⏭   Examples
 ✅  Coverage
 ✅  Fuzzing
 ✅  Stateful

Test cases: X generated, X passed
```

### 6.3 Troubleshooting

| Issue | Possible Cause | Solution |
|-------|----------------|----------|
| "No links found" | OperationIdTransformer not registered | Check transformer registration order |
| "Unknown operationId" | Link references wrong ID | Verify OperationIds dictionary |
| "Cannot resolve expression" | Wrong JSON path | Check response structure |
| Stateful tests fail | Missing resources | Ensure database is seeded |

---

## 7. Best Practices

### 7.1 Transformer Order

**Critical**: Register transformers in the correct order:

```csharp
// 1. First: Add operationIds (required by links)
options.AddDocumentTransformer<OperationIdTransformer>();

// 2. Second: Add links (references operationIds)
options.AddDocumentTransformer<OpenApiLinksTransformer>();
```

### 7.2 Runtime Expression Guidelines

| Do | Don't |
|----|-------|
| Use JSON Pointer syntax (`#/data/id`) | Use dot notation (`data.id`) |
| Match exact response structure | Guess at paths |
| Test expressions manually | Assume they work |

### 7.3 Link Naming Conventions

| Pattern | Example |
|---------|---------|
| Get{Resource} | GetCreatedAccount |
| Update{Resource} | UpdateCreatedAccount |
| Delete{Resource} | DeleteCreatedAccount |
| {Verb}{Resource} | SetCreatedAccountPrimary |

### 7.4 Response Structure Compatibility

Ensure your API response structure supports link extraction:

```json
// GOOD - Clear path to ID
{
  "data": {
    "id": "guid-here"
  }
}

// BAD - ID buried in nested structure
{
  "result": {
    "items": [
      { "account": { "identifier": { "value": "guid" } } }
    ]
  }
}
```

---

## Appendix A: Complete Transformer Registration

```csharp
// In ServiceCollectionExtensions.cs AddOpenApi method

services.AddOpenApi("v1", options =>
{
    // === Schema Transformers ===
    options.AddSchemaTransformer<DataAnnotationSchemaTransformer>();
    options.AddSchemaTransformer<IntegerSchemaTransformer>();
    options.AddSchemaTransformer<JsonStringEnumSchemaTransformer>();

    // === Operation Transformers ===
    options.AddOperationTransformer<AnonymousEndpointTransformer>();
    options.AddOperationTransformer<AuthorizationResponseTransformer>();
    options.AddOperationTransformer<NotFoundResponseTransformer>();
    options.AddOperationTransformer<ValidationResponseTransformer>();

    // === Document Transformers ===
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
    options.AddDocumentTransformer<QueryParameterConstraintsTransformer>();
    options.AddDocumentTransformer<RequestBodySchemaConstraintsTransformer>();
    options.AddDocumentTransformer<BusinessRulesDocumentTransformer>();

    // === NEW: Links Support ===
    options.AddDocumentTransformer<OperationIdTransformer>();     // Must be before Links
    options.AddDocumentTransformer<OpenApiLinksTransformer>();
});
```

---

## Appendix B: JSON Schema for Links Validation

To validate your OpenAPI spec has correct links:

```json
{
  "type": "object",
  "required": ["operationId"],
  "properties": {
    "operationId": { "type": "string" },
    "parameters": {
      "type": "object",
      "additionalProperties": { "type": "string" }
    }
  }
}
```

---

*End of Document*
