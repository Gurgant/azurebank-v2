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
# From backend folder (where openapiv1.json is located)
cd backend
schemathesis run ./openapiv1.json --url http://localhost:5068
```

### 3. Run with Configuration File (v4 TOML format)

```bash
# Copy schemathesis.toml to backend folder, then run:
schemathesis run ./openapiv1.json
```

## Test Options

### Basic Run
```bash
schemathesis run ./openapiv1.json --url http://localhost:5068
```

### With Authentication
```bash
schemathesis run ./openapiv1.json \
  --url http://localhost:5068 \
  --hooks tests/contract/hooks.py
```

### Verbose Output
```bash
schemathesis run ./openapiv1.json \
  --url http://localhost:5068 \
  --verbosity 2
```

### Specific Endpoint
```bash
schemathesis run ./openapiv1.json \
  --url http://localhost:5068 \
  --endpoint "/api/auth/.*"
```

### Generate Report
```bash
schemathesis run ./openapiv1.json \
  --url http://localhost:5068 \
  --report
```

### CI/CD Mode (JUnit output)
```bash
schemathesis run ./openapiv1.json \
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

```
==================== Schemathesis test session starts ====================
Schema location: openapiv1.json
Base URL: http://localhost:5068
Collected API operations: 20

POST /api/auth/register ....                                       [  5%]
POST /api/auth/login ....                                          [ 10%]
GET /api/auth/me ....                                              [ 15%]
POST /api/auth/logout ....                                         [ 20%]
POST /api/auth/pin ....                                            [ 25%]
POST /api/auth/pin/verify ....                                     [ 30%]
GET /api/accounts ....                                             [ 35%]
POST /api/accounts ....                                            [ 40%]
GET /api/accounts/{id} ....                                        [ 45%]
PATCH /api/accounts/{id} ....                                      [ 50%]
DELETE /api/accounts/{id} ....                                     [ 55%]
GET /api/accounts/{id}/balance ....                                [ 60%]
PATCH /api/accounts/{id}/set-primary ....                          [ 65%]
GET /api/transactions ....                                         [ 70%]
GET /api/transactions/{id} ....                                    [ 75%]
POST /api/transactions/deposit ....                                [ 80%]
POST /api/transactions/withdraw ....                               [ 85%]
POST /api/transfers ....                                           [ 90%]
POST /api/transfers/internal ....                                  [ 95%]
GET /api/users/search ....                                         [100%]

======================== 847 passed in 45.23s ============================
```

## Troubleshooting

### SSL Certificate Errors
```bash
# Add --tls-verify=false for localhost
schemathesis run ./openapiv1.json --url http://localhost:5068 --tls-verify=false
```

### Rate Limiting
```bash
# Reduce concurrency
schemathesis run ./openapiv1.json --workers=1
```

### Timeout Issues
```bash
# Increase timeout
schemathesis run ./openapiv1.json --request-timeout=30000
```
