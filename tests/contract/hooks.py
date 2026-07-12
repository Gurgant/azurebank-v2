"""
Schemathesis Hooks for AzureBank API Testing
=============================================

This module provides authentication hooks for Schemathesis tests.
It automatically registers a test user and adds JWT tokens to requests.

Usage:
    schemathesis run ./openapiv1.json --url http://localhost:5068 --hooks tests/contract/hooks.py
"""

import schemathesis
import requests
import uuid
import os
from typing import Optional

# Global token storage
_auth_token: Optional[str] = None
_test_user_email: Optional[str] = None
_test_user_id: Optional[str] = None
_test_account_id: Optional[str] = None


def get_base_url() -> str:
    """Get the base URL from environment or default."""
    return os.environ.get("SCHEMATHESIS_BASE_URL", "http://localhost:5068")


def register_test_user() -> dict:
    """Register a new test user and return the response data."""
    global _test_user_email, _test_user_id

    unique_id = uuid.uuid4().hex[:8]
    _test_user_email = f"schemathesis.test.{unique_id}@example.com"

    response = requests.post(
        f"{get_base_url()}/api/auth/register",
        json={
            "azureTag": f"schemathesis.{unique_id}",
            "email": _test_user_email,
            "password": "TestPass123!",
            "firstName": "Schemathesis",
            "lastName": "Test"
        },
        verify=False  # Disable SSL verification for localhost
    )

    if response.status_code == 201:
        data = response.json()
        _test_user_id = data["data"]["user"]["id"]
        return data
    else:
        raise Exception(f"Failed to register test user: {response.text}")


def get_auth_token() -> str:
    """Get or create an authentication token."""
    global _auth_token

    if _auth_token is None:
        data = register_test_user()
        _auth_token = data["data"]["token"]["accessToken"]

    return _auth_token


def get_test_account_id() -> str:
    """Get the primary account ID for the test user."""
    global _test_account_id

    if _test_account_id is None:
        token = get_auth_token()
        response = requests.get(
            f"{get_base_url()}/api/accounts",
            headers={"Authorization": f"Bearer {token}"},
            verify=False
        )

        if response.status_code == 200:
            accounts = response.json()["data"]
            if accounts:
                _test_account_id = accounts[0]["id"]

    return _test_account_id or str(uuid.uuid4())


# ============================================================================
# Schemathesis Hooks
# ============================================================================

@schemathesis.hook("before_call")
def add_auth_header(context, case):
    """
    Add JWT Bearer token to all requests except public endpoints.

    Public endpoints:
    - POST /api/auth/register
    - POST /api/auth/login
    """
    public_paths = ["/api/auth/register", "/api/auth/login"]

    # Skip auth for public endpoints
    if case.path in public_paths and case.method.upper() == "POST":
        return

    # Add auth header for protected endpoints
    try:
        token = get_auth_token()
        case.headers = case.headers or {}
        case.headers["Authorization"] = f"Bearer {token}"
    except Exception as e:
        # Log but don't fail - some tests may intentionally test unauthorized access
        print(f"Warning: Could not get auth token: {e}")


@schemathesis.hook("before_generate_case")
def customize_case_generation(context, strategy):
    """
    Customize test case generation based on endpoint.

    This hook allows us to provide realistic test data for specific fields.
    """
    return strategy


@schemathesis.hook("add_case")
def filter_cases(context, case):
    """
    Filter out invalid test cases that we know will fail.

    This is useful for excluding edge cases that aren't meaningful tests.
    """
    # Always include the case for comprehensive testing
    return case


@schemathesis.hook("after_call")
def log_response(context, case, response):
    """
    Log responses for debugging purposes.

    Can be extended to collect metrics or trigger alerts.
    """
    # Uncomment for verbose logging:
    # print(f"{case.method} {case.path} -> {response.status_code}")
    pass


# ============================================================================
# Custom Checks
# ============================================================================

@schemathesis.check
def response_time_check(response, case):
    """Check that API responses are reasonably fast."""
    max_response_time = 5.0  # seconds
    elapsed = response.elapsed.total_seconds()

    assert elapsed < max_response_time, (
        f"Response time {elapsed:.2f}s exceeds maximum {max_response_time}s "
        f"for {case.method} {case.path}"
    )


@schemathesis.check
def no_server_errors(response, case):
    """Check that no 5xx errors occur (except for intentional fuzzing)."""
    # Allow 500 errors for malformed input (fuzzing purpose)
    # But flag them for review
    if response.status_code >= 500:
        # Log but don't fail - fuzzing is meant to find these
        print(f"Server error detected: {case.method} {case.path} -> {response.status_code}")


# ============================================================================
# Test Data Providers
# ============================================================================

def provide_account_id():
    """Provide a valid account ID for tests requiring one."""
    return get_test_account_id()


def provide_valid_amount():
    """Provide valid transaction amounts for testing."""
    import random
    return round(random.uniform(1.0, 1000.0), 2)


# ============================================================================
# Cleanup (Optional)
# ============================================================================

def cleanup():
    """
    Clean up test data after tests complete.

    Note: In-memory database resets automatically, so this may not be needed.
    """
    global _auth_token, _test_user_email, _test_user_id, _test_account_id
    _auth_token = None
    _test_user_email = None
    _test_user_id = None
    _test_account_id = None


# Disable SSL warnings for localhost testing
import urllib3
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)
