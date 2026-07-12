"""
Unit tests for Schemathesis hooks.

Run with: python -m pytest schemathesis/test_hooks.py -v
"""

import pytest
from hooks import filter_body, filter_query, _has_extreme_timezone_offset


class MockOperation:
    """Mock Schemathesis operation object."""
    def __init__(self, path: str, method: str):
        self.path = path
        self.method = method


class MockContext:
    """Mock Schemathesis context object."""
    def __init__(self, path: str, method: str):
        self.operation = MockOperation(path, method)


class TestFilterBody:
    """Tests for filter_body hook."""

    def test_allows_different_accounts(self):
        """Different account IDs should be allowed."""
        ctx = MockContext("/api/transfers/internal", "POST")
        body = {
            "fromAccountId": "account-1",
            "toAccountId": "account-2",
            "amount": 100
        }
        assert filter_body(ctx, body) is True

    def test_filters_same_accounts(self):
        """Same account IDs should be filtered out."""
        ctx = MockContext("/api/transfers/internal", "POST")
        body = {
            "fromAccountId": "account-1",
            "toAccountId": "account-1",  # SAME!
            "amount": 100
        }
        assert filter_body(ctx, body) is False

    def test_filters_same_accounts_with_guids(self):
        """Same GUIDs should be filtered."""
        ctx = MockContext("/api/transfers/internal", "POST")
        guid = "550e8400-e29b-41d4-a716-446655440000"
        body = {
            "fromAccountId": guid,
            "toAccountId": guid,
            "amount": 100
        }
        assert filter_body(ctx, body) is False

    def test_allows_none_body(self):
        """None body should be allowed (edge case)."""
        ctx = MockContext("/api/transfers/internal", "POST")
        assert filter_body(ctx, None) is True

    def test_allows_other_endpoints(self):
        """Other endpoints should not be filtered."""
        ctx = MockContext("/api/accounts", "POST")
        body = {"name": "Savings", "accountType": "Savings"}
        assert filter_body(ctx, body) is True

    def test_filters_empty_azure_tag(self):
        """Empty azureTag in external transfer should be filtered."""
        ctx = MockContext("/api/transfers", "POST")
        body = {
            "fromAccountId": "account-1",
            "recipientAzureTag": "",
            "amount": 100
        }
        assert filter_body(ctx, body) is False

    def test_allows_valid_azure_tag(self):
        """Valid azureTag should be allowed."""
        ctx = MockContext("/api/transfers", "POST")
        body = {
            "fromAccountId": "account-1",
            "recipientAzureTag": "john-azure-tag",
            "amount": 100
        }
        assert filter_body(ctx, body) is True


class TestFilterQuery:
    """Tests for filter_query hook."""

    def test_allows_normal_page(self):
        """Normal page numbers should be allowed."""
        ctx = MockContext("/api/transactions", "GET")
        query = {"page": 1, "pageSize": 20}
        assert filter_query(ctx, query) is True

    def test_filters_overflow_page(self):
        """Extremely large page numbers should be filtered."""
        ctx = MockContext("/api/transactions", "GET")
        query = {"page": 2147483647}  # Int32.MaxValue
        assert filter_query(ctx, query) is False

    def test_filters_negative_page(self):
        """Negative page numbers should be filtered."""
        ctx = MockContext("/api/transactions", "GET")
        query = {"page": -1}
        assert filter_query(ctx, query) is False

    def test_filters_extreme_page_size(self):
        """Extremely large page sizes should be filtered."""
        ctx = MockContext("/api/transactions", "GET")
        query = {"pageSize": 10000}
        assert filter_query(ctx, query) is False

    def test_filters_extreme_timezone(self):
        """Extreme timezone offsets should be filtered."""
        ctx = MockContext("/api/transactions", "GET")
        query = {"fromDate": "2024-01-15T10:30:00-23:41"}  # Invalid offset
        assert filter_query(ctx, query) is False

    def test_allows_valid_timezone(self):
        """Valid timezone offsets should be allowed."""
        ctx = MockContext("/api/transactions", "GET")
        query = {"fromDate": "2024-01-15T10:30:00+05:30"}  # India
        assert filter_query(ctx, query) is True

    def test_allows_none_query(self):
        """None query should be allowed."""
        ctx = MockContext("/api/transactions", "GET")
        assert filter_query(ctx, None) is True

    def test_allows_boundary_page(self):
        """Boundary page numbers should be allowed."""
        ctx = MockContext("/api/transactions", "GET")
        query = {"page": 1000000}  # MAX_PAGE_NUMBER
        assert filter_query(ctx, query) is True


class TestTimezoneOffset:
    """Tests for _has_extreme_timezone_offset helper."""

    def test_valid_positive_offsets(self):
        """Valid positive offsets should return False."""
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00+00:00") is False
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00+05:30") is False
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00+09:00") is False
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00+14:00") is False

    def test_valid_negative_offsets(self):
        """Valid negative offsets should return False."""
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00-00:00") is False
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00-05:00") is False
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00-08:00") is False
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00-12:00") is False

    def test_extreme_positive_offsets(self):
        """Extreme positive offsets should return True."""
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00+15:00") is True
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00+20:00") is True
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00+23:59") is True

    def test_extreme_negative_offsets(self):
        """Extreme negative offsets should return True."""
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00-15:00") is True
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00-23:41") is True

    def test_no_offset(self):
        """Datetimes without offset should return False."""
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00") is False
        assert _has_extreme_timezone_offset("2024-01-15T10:30:00Z") is False

    def test_empty_string(self):
        """Empty string should return False."""
        assert _has_extreme_timezone_offset("") is False
        assert _has_extreme_timezone_offset(None) is False


if __name__ == "__main__":
    pytest.main([__file__, "-v"])
