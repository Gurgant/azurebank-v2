# Contract Testing with Schemathesis

## Overview

This folder contains configuration for **Schemathesis**, an automatic API testing tool that generates comprehensive test suites from OpenAPI specifications.

## Prerequisites

```bash
# Install Schemathesis
pip install schemathesis

# Or use Docker (no Python needed)
docker pull schemathesis/schemathesis:stable
```

## Quick Start

### 1. Start the API

```bash
cd backend/src/AzureBank.Api
dotnet run
```

### 2. Run Auto-Generated Tests

```bash
# From the repository root. The document lives at docs/api/openapiv1.json and never
# lived in backend/ -- `git log --all -- backend/openapiv1.json` is empty. This is the
# command CI runs (.github/workflows/ci.yml, the conformance job).
schemathesis run docs/api/openapiv1.json --url http://localhost:5068
```

### 3. Run with Configuration File (v4 TOML format)

```bash
# The configuration lives beside this file, at tests/contract/schemathesis.toml.
schemathesis run docs/api/openapiv1.json --config-file tests/contract/schemathesis.toml
```

## Test Options

### Basic Run
```bash
schemathesis run docs/api/openapiv1.json --url http://localhost:5068
```

### With Authentication
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --hooks tests/contract/hooks.py
```

### Verbose Output
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --verbosity 2
```

### Specific Endpoint
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --endpoint "/api/auth/.*"
```

### Generate Report
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --report
```

### CI/CD Mode (JUnit output)
```bash
schemathesis run docs/api/openapiv1.json \
  --url http://localhost:5068 \
  --junit-xml=test-results.xml
```

## What Gets Tested

Schemathesis automatically:

1. **Parses OpenAPI spec** - Reads all endpoint definitions
2. **Generates test cases** - Creates hundreds of inputs per endpoint
3. **Tests edge cases** - Boundary values, nulls, empty strings
4. **Fuzz tests** - Malformed data, special characters
5. **Validates responses** - Schema compliance, status codes
6. **Finds bugs** - 500 errors, validation bypasses, crashes

## Files

| File | Purpose |
|------|---------|
| `schemathesis.toml` | Main configuration file (v4 format) |
| `schemathesis.yaml` | Legacy configuration (v3 format) |
| `hooks.py` | Python hooks for authentication |
| `README.md` | This documentation |

## Expected Output

From the `Runtime conformance` job on `main`, run 35581244192, 2026-09-21 — the tail of a real
run rather than an illustration:

```
=================================== SUMMARY ====================================
============================= 2 warnings in 54.61s =============================
27 operations tested, 28 test cases in the report
```

The operation count comes from the committed document, so it moves when the contract does; the
step after the run fails the job if it drops below the floor.

*(What stood here until 2026-09-21 was an invented transcript: it announced `Collected API
operations: 20` against a document that declares 27, listed `GET /api/users/search` — a route
ADR-0014 deleted on 2026-07-17 — and ended `847 passed in 45.23s`, a figure no run produced. It
was also the reason the Bruno collection called that same dead endpoint, found in #196.)*

## Troubleshooting

### SSL Certificate Errors
```bash
# Add --tls-verify=false for localhost
schemathesis run docs/api/openapiv1.json --url http://localhost:5068 --tls-verify=false
```

### Rate Limiting
```bash
# Reduce concurrency
schemathesis run docs/api/openapiv1.json --workers=1
```

### Timeout Issues
```bash
# Increase timeout
schemathesis run docs/api/openapiv1.json --request-timeout=30000
```
