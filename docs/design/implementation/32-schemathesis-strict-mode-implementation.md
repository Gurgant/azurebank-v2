# Schemathesis Strict Mode Implementation Guide

> **Document Version**: 1.0
> **Created**: 2026-01-14
> **Status**: Active Implementation
> **Related**: [30-business-rule-validation-implementation-plan.md](./30-business-rule-validation-implementation-plan.md)

---

## Table of Contents

1. [Executive Summary](#1-executive-summary)
2. [Problem Statement](#2-problem-statement)
3. [Test Results Analysis](#3-test-results-analysis)
4. [Enterprise Best Practices Research](#4-enterprise-best-practices-research)
5. [Root Cause Diagnosis](#5-root-cause-diagnosis)
6. [Solution Architecture](#6-solution-architecture)
7. [Implementation Plan](#7-implementation-plan)
8. [Best Practices](#8-best-practices)
9. [References](#9-references)

---

## 1. Executive Summary

### 1.1 Context

AzureBank API uses Schemathesis v4.8.0 for contract testing. The project moved from **permissive mode** (accepting all 4xx responses) to **strict mode** (expecting only 2xx responses) to catch real schema problems instead of masking them.

### 1.2 Key Decision

| Mode | Configuration | Purpose |
|------|---------------|---------|
| **Permissive** | `expected-statuses = [200, 201, 204, 400, 401, 403, 404, 409, 422]` | Verify API doesn't crash |
| **Strict** | `expected-statuses = [200, 201, 204]` | Verify API works correctly |

### 1.3 Current Challenge

With strict mode enabled, 13 tests fail because:
- Random UUIDs don't exist in database (404)
- Random credentials fail authentication (401)
- Random accounts don't belong to user (403)
- Duplicate unique values cause conflicts (409)

### 1.4 Solution Approach

Two-pronged approach:
1. **OpenAPI Links** - Enable stateful testing for CRUD workflows
2. **Python Hooks** - Inject real test data where links don't apply

---

## 2. Problem Statement

### 2.1 The Masking Problem

When using permissive mode with many expected status codes, Schemathesis masks real schema problems:

```toml
# PERMISSIVE - Masks problems
positive_data_acceptance.expected-statuses = [200, 201, 204, 400, 401, 403, 404, 409, 422]
```

**Example**: If the schema allows passwords with only 4 characters but API requires 8, Schemathesis generates 4-char passwords, gets 400, but doesn't report it as a failure because 400 is "expected."

### 2.2 The Stateless Problem

Schemathesis is stateless by default. It generates random values without knowledge of:
- What resources exist in the database
- What credentials are valid
- What account IDs belong to the authenticated user

### 2.3 The Goal

**Catch real schema mismatches** while **not failing on expected 4xx scenarios** that are impossible to avoid with random data.

---

## 3. Test Results Analysis

### 3.1 Strict Mode Test Results (2026-01-14)

```
Total Operations: 21
Coverage: 20 passed, 1 failed (95.2%)
Fuzzing: 8 passed, 13 failed (38.1%)
Test Cases: 1,763 generated
Duration: 119.22s
```

### 3.2 Failure Classification

| Status Code | Count | Percentage | Root Cause |
|-------------|-------|------------|------------|
| 404 Not Found | 10 | 76.9% | Random UUID doesn't exist |
| 401 Unauthorized | 1 | 7.7% | Random credentials invalid |
| 403 Forbidden | 1 | 7.7% | Account belongs to different user |
| 409 Conflict | 1 | 7.7% | Duplicate email/azureTag |

### 3.3 Detailed Failure Breakdown

#### 3.3.1 Category A: 404 Not Found (10 failures)

| Endpoint | Issue |
|----------|-------|
| GET /api/accounts/{id} | Random UUID doesn't exist |
| PATCH /api/accounts/{id} | Random UUID doesn't exist |
| DELETE /api/accounts/{id} | Random UUID doesn't exist |
| GET /api/accounts/{id}/balance | Random UUID doesn't exist |
| PATCH /api/accounts/{id}/set-primary | Random UUID doesn't exist |
| GET /api/transactions/{id} | Random UUID doesn't exist |
| POST /api/transactions/deposit | Random accountId in body |
| POST /api/transactions/withdraw | Random accountId in body |
| POST /api/transfers | Random fromAccountId in body |
| POST /api/transfers/internal | Random fromAccountId/toAccountId |

**Solution**: OpenAPI Links + map_path_parameters/map_body hooks

#### 3.3.2 Category B: 401 Unauthorized (1 failure)

| Endpoint | Issue |
|----------|-------|
| POST /api/auth/login | Random credentials don't exist |

**Solution**: Skip endpoint in filter_body hook (random credentials MUST fail)

#### 3.3.3 Category C: 403 Forbidden (1 failure)

| Endpoint | Issue |
|----------|-------|
| GET /api/transactions?AccountId=... | Random account not owned by user |

**Solution**: map_query hook to inject real account ID

#### 3.3.4 Category D: 409 Conflict (1 failure)

| Endpoint | Issue |
|----------|-------|
| POST /api/auth/register | Duplicate email generated |

**Solution**: map_body hook with unique counter

---

## 4. Enterprise Best Practices Research

### 4.1 Industry Sources

| Source | Key Insight |
|--------|-------------|
| [Capital One](https://www.capitalone.com/tech/software-engineering/api-testing-schemathesis/) | Shift-left testing, catch issues early |
| [Schemathesis Docs](https://schemathesis.readthedocs.io/) | Hooks for data injection, stateful testing |
| [Microsoft .NET 10](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/) | IOpenApiDocumentProvider, OpenAPI 3.1 |
| [HyperTest 2025](https://www.hypertest.co/contract-testing/) | 85% enterprises increasing microservices |

### 4.2 Two-Tier Testing Strategy

| Tier | Purpose | Expected Status | Approach |
|------|---------|-----------------|----------|
| Tier 1 | Schema Compliance | 2xx, 4xx | Permissive |
| Tier 2 | Business Logic | 2xx only | Strict + Hooks |

### 4.3 Hook Execution Order

```
filter_* → map_* → flatmap_* → Final test case
```

| Hook Type | Purpose | Return Value |
|-----------|---------|--------------|
| `filter_*` | Skip test cases | `True` keep, `False` skip |
| `map_*` | Transform values | Modified value or `None` to skip |
| `flatmap_*` | Generate with strategy | Hypothesis strategy |

### 4.4 Stateful Testing with OpenAPI Links

OpenAPI Links enable chaining operations:

```
POST /accounts → Create account → Extract ID
GET /accounts/{id} → Use extracted ID → 200 OK
```

This eliminates 404 errors by using real data from previous operations.

---

## 5. Root Cause Diagnosis

### 5.1 Fundamental Problem

Schemathesis generates **schema-valid** but **business-invalid** data:
- Valid UUID format → But doesn't exist in database
- Valid email format → But not registered
- Valid password format → But wrong password

### 5.2 Problem Categories

| Code | Problem | Impact | Solution |
|------|---------|--------|----------|
| P1 | Random UUIDs don't exist | 404 | OpenAPI Links / map_path_parameters |
| P2 | Random credentials fail | 401 | Skip login endpoint |
| P3 | Random account not owned | 403 | map_query with real account |
| P4 | Duplicate unique values | 409 | Unique counter in map_body |
| P5 | Business rule violations | 422 | filter_body |

### 5.3 Current hooks.py Gap Analysis

| Required Hook | Status | Implementation Needed |
|---------------|--------|----------------------|
| `map_path_parameters` | Missing | Inject real IDs |
| `map_body` | Missing | Inject real account IDs |
| `map_query` | Missing | Inject real account ID for filter |
| `filter_body` (login) | Missing | Skip login endpoint |
| Unique generation | Missing | Counter-based email/azureTag |

---

## 6. Solution Architecture

### 6.1 Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    SCHEMATHESIS TESTING LAYER                    │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────────────┐    ┌──────────────────┐                   │
│  │  OpenAPI Links   │    │   Python Hooks   │                   │
│  │  (Stateful)      │    │   (Data Inject)  │                   │
│  └────────┬─────────┘    └────────┬─────────┘                   │
│           │                       │                              │
│           ▼                       ▼                              │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │              Test Case Generation                         │   │
│  │  1. Generate schema-valid data                            │   │
│  │  2. Apply filter_* hooks (skip invalid)                   │   │
│  │  3. Apply map_* hooks (inject real data)                  │   │
│  │  4. Execute request                                       │   │
│  │  5. Validate response (expect 2xx)                        │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    OPENAPI SPECIFICATION                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────────────┐    ┌──────────────────┐                   │
│  │  Schema          │    │  Links           │                   │
│  │  Transformers    │    │  Transformer     │                   │
│  └────────┬─────────┘    └────────┬─────────┘                   │
│           │                       │                              │
│           ▼                       ▼                              │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │              Generated OpenAPI 3.1 Spec                   │   │
│  │  • Validation constraints                                 │   │
│  │  • Business rules documentation                           │   │
│  │  • Response links for CRUD workflows                      │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    ASP.NET CORE 10 API                           │
├─────────────────────────────────────────────────────────────────┤
│  Controllers → Services → Database (Seeded Test Data)           │
└─────────────────────────────────────────────────────────────────┘
```

### 6.2 OpenAPI Links Strategy

| Create Operation | Response Link | Linked Operation |
|------------------|---------------|------------------|
| POST /api/accounts | GetAccount | GET /api/accounts/{id} |
| POST /api/accounts | UpdateAccount | PATCH /api/accounts/{id} |
| POST /api/accounts | DeleteAccount | DELETE /api/accounts/{id} |
| POST /api/accounts | GetBalance | GET /api/accounts/{id}/balance |
| POST /api/transactions/deposit | GetTransaction | GET /api/transactions/{id} |
| POST /api/auth/register | GetCurrentUser | GET /api/auth/me |

### 6.3 Python Hooks Strategy

| Hook | Endpoints | Action |
|------|-----------|--------|
| `filter_body` | /api/auth/login | Skip (can't test with random creds) |
| `map_body` | /api/auth/register | Inject unique email/azureTag |
| `map_body` | /api/transactions/* | Inject real accountId |
| `map_body` | /api/transfers/* | Inject real fromAccountId |
| `map_path_parameters` | /api/accounts/{id} | Inject real account ID |
| `map_query` | /api/transactions | Inject real AccountId filter |

---

## 7. Implementation Plan

### Phase 1: OpenAPI Links Transformer

**Goal**: Add links to OpenAPI spec for stateful testing

**Files to Create/Modify**:
- `Transformers/OpenApiLinksTransformer.cs` (new)
- `Extensions/ServiceCollectionExtensions.cs` (register transformer)

**Links to Add**:

```yaml
# POST /api/accounts response
links:
  GetAccount:
    operationId: GetAccountById
    parameters:
      id: $response.body#/data/id
  UpdateAccount:
    operationId: UpdateAccount
    parameters:
      id: $response.body#/data/id
  DeleteAccount:
    operationId: DeleteAccount
    parameters:
      id: $response.body#/data/id
  GetAccountBalance:
    operationId: GetAccountBalance
    parameters:
      id: $response.body#/data/id
```

### Phase 2: Enhanced Python Hooks

**Goal**: Handle scenarios where links don't apply

**File**: `hooks.py`

```python
# TestDataManager for centralized data
class TestDataManager:
    def __init__(self):
        self._initialized = False
        self._account_ids = []
        self._transaction_ids = []
        self._unique_counter = 0

    def get_account_id(self) -> str:
        self.initialize()
        return self._account_ids[0] if self._account_ids else None

    def generate_unique_email(self) -> str:
        self._unique_counter += 1
        return f"schemathesis_{self._unique_counter}@test.com"

# Hook implementations
@schemathesis.hook
def filter_body(ctx, body):
    # Skip login - random credentials MUST fail
    if ctx.operation.path == "/api/auth/login":
        return False
    return True

@schemathesis.hook
def map_body(ctx, body):
    if body is None:
        return body
    # Inject real data based on endpoint
    ...
    return body
```

### Phase 3: Testing & Validation

**Commands**:
```bash
# Regenerate OpenAPI spec
dotnet run --project src/AzureBank.Api -- --export-openapi

# Test with stateful mode
schemathesis run ./openapiv1.json --stateful=links --url http://localhost:5068

# Verify all tests pass
# Expected: 0 failures
```

---

## 8. Best Practices

### 8.1 Testing Strategy

| Practice | Description |
|----------|-------------|
| Use seeded test data | Pre-populate DB with known fixtures |
| Separate test user | Dedicated user for Schemathesis |
| Idempotent tests | Don't depend on execution order |
| Clean state per run | Use in-memory DB or reset |

### 8.2 Hook Development

| Practice | Description |
|----------|-------------|
| Lazy initialization | Fetch data on first use only |
| Cache aggressively | Don't re-fetch on every call |
| Return None to skip | `map_*` returns None to skip |
| Log skipped tests | Print warnings for visibility |

### 8.3 OpenAPI Schema

| Practice | Description |
|----------|-------------|
| Embed constraints | Use patterns for length constraints |
| Provide examples | Realistic data in spec |
| Define all links | Enable stateful testing |
| Document all 4xx | Every error in spec |

### 8.4 .NET 10 Specific

| Practice | Description |
|----------|-------------|
| Use OpenAPI 3.1 | Default in .NET 10 |
| Use Scalar UI | Modern alternative to Swagger |
| Use transformers | Fix schema at generation time |
| Use IOpenApiDocumentProvider | Programmatic access |

---

## 9. References

### 9.1 Official Documentation

- [Schemathesis Documentation](https://schemathesis.readthedocs.io/)
- [Schemathesis Extending Guide](https://schemathesis.readthedocs.io/en/stable/guides/extending/)
- [Schemathesis Hooks Reference](https://schemathesis.readthedocs.io/en/latest/reference/hooks/)
- [Schemathesis Checks Reference](https://schemathesis.readthedocs.io/en/stable/reference/checks/)
- [OpenAPI 3.1 Specification](https://spec.openapis.org/oas/v3.1.0)
- [Microsoft OpenAPI in .NET 10](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/)

### 9.2 Industry Resources

- [Capital One: API Testing with Schemathesis](https://www.capitalone.com/tech/software-engineering/api-testing-schemathesis/)
- [HyperTest: Contract Testing Tools 2025](https://www.hypertest.co/contract-testing/best-api-contract-testing-tools)
- [Schemathesis GitHub](https://github.com/schemathesis/schemathesis)

### 9.3 Related Project Documents

- [30-business-rule-validation-implementation-plan.md](./30-business-rule-validation-implementation-plan.md)
- [06-api-contracts.md](./06-api-contracts.md)
- [08-security-design.md](./08-security-design.md)

---

## Appendix A: Configuration Files

### A.1 schemathesis.toml (Strict Mode)

```toml
# Schemathesis v4.8.0 - STRICT MODE Configuration

hooks = "hooks"
workers = 1
seed = 42
request-timeout = 30
continue-on-failure = true
tls-verify = false

[generation]
max-examples = 100
mode = "positive"
deterministic = true

[checks]
# STRICT MODE: Only accept success responses
positive_data_acceptance.expected-statuses = [200, 201, 204]
```

### A.2 hooks.py (Template)

```python
"""
Schemathesis Hooks for AzureBank API - Strict Mode
===================================================
Compatible with Schemathesis v4.8.0+

This module provides data injection for strict mode testing.
"""

import schemathesis
import requests
from typing import Optional, List

class TestDataManager:
    """Centralized test data management."""

    _instance = None

    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
            cls._instance._initialized = False
        return cls._instance

    # ... implementation

test_data = TestDataManager()

@schemathesis.hook
def filter_body(ctx, body):
    """Skip untestable endpoints."""
    if ctx.operation.path == "/api/auth/login":
        return False
    return True

@schemathesis.hook
def map_body(ctx, body):
    """Inject real test data into request bodies."""
    # ... implementation
    return body

@schemathesis.hook
def map_path_parameters(ctx, path_parameters):
    """Replace random UUIDs with real resource IDs."""
    # ... implementation
    return path_parameters
```

---

## Appendix B: Test Execution Commands

```bash
# Standard strict mode test
schemathesis run ./openapiv1.json --url http://localhost:5068

# With stateful testing (requires OpenAPI Links)
schemathesis run ./openapiv1.json --stateful=links --url http://localhost:5068

# With explicit hooks
schemathesis run ./openapiv1.json --hooks hooks --url http://localhost:5068

# Generate JUnit report for CI/CD
schemathesis run ./openapiv1.json --report junit-xml --output results.xml

# Verbose output for debugging
schemathesis run ./openapiv1.json --verbosity 2
```

---

*End of Document*
