# Review Notes
## Team Discussion Outcomes

**Document Version**: 1.0
**Created**: 2025-12-16
**Status**: ONGOING

---

## 1. Overview

This document captures all team discussions, confrontations, and review outcomes throughout the project.

---

## 2. Phase 0 Notes

### 2.1 Initialization Review
- **Date**: 2025-12-16
- **Participants**: System Architect
- **Outcome**: Project structure initialized successfully

---

## 3. Phase 1 Notes

```
[TODO: Add notes as Phase 1 progresses]
```

---

## 4. Phase 2 Notes

### 4.1 UX/UI Expert <-> Web Designer Confrontation
```
[TODO: Document in Phase 2.4.1]
```

### 4.2 Designer Team <-> Frontend Lead Confrontation
```
[TODO: Document in Phase 2.4.2]
```

### 4.3 Claude Team <-> Gemini Review Confrontation
```
[TODO: Document in Phase 2.6.1]
```

---

## 5. Phase 3 Notes

### 5.1 Database Engineer <-> Backend Lead Confrontation

**Date**: 2026-01-08
**Participants**: Database Engineer, Backend Lead
**Status**: COMPLETE

---

#### Discussion Topics & Resolutions

##### Topic 1: Normalization Level

**Database Engineer Position**:
> Schema is at 3NF (Third Normal Form). All non-key attributes depend only on the primary key. No transitive dependencies.

**Backend Lead Position**:
> Agree with 3NF. For this application scale, further normalization would add complexity without significant benefit. The `BalanceAfter` in Transactions is denormalized but intentional for audit trail and performance.

**Resolution**: **APPROVED** - 3NF with intentional denormalization for `BalanceAfter`

---

##### Topic 2: Decimal Precision for Money

**Database Engineer Position**:
> Using `DECIMAL(19,4)` which supports up to 15 digits before decimal and 4 after. This handles values up to 999,999,999,999,999.9999 EUR.

**Backend Lead Position**:
> 4 decimal places is overkill for EUR which only uses 2. However, it provides flexibility for:
> - Future currency support with more decimals
> - Precision in interest calculations
> - No rounding errors in complex operations

**Resolution**: **APPROVED** - Keep `DECIMAL(19,4)` for future-proofing

---

##### Topic 3: Balance Calculation Strategy

**Database Engineer Position**:
> Two strategies available:
> 1. **Stored Balance**: Keep `Balance` column in Accounts, update on each transaction
> 2. **Calculated Balance**: Sum all transactions to get current balance

**Backend Lead Position**:
> Stored Balance is correct for this use case:
> - Real-time balance display needs O(1) lookup
> - Transaction history can get very large
> - Must use database transactions to ensure atomicity

**Concerns Raised**:
> - Race conditions during concurrent updates
> - Need optimistic locking or row-level locking

**Resolution**: **APPROVED** - Stored Balance with following requirements:
1. Use `SERIALIZABLE` isolation level for balance-modifying operations
2. Or implement `RowVersion` column for optimistic concurrency
3. Document in backend implementation guide

---

##### Topic 4: Soft Deletes Strategy

**Database Engineer Position**:
> All entities have `IsDeleted` flag instead of hard deletes. Benefits:
> - Audit trail preservation
> - Data recovery possible
> - Referential integrity maintained

**Backend Lead Position**:
> Agree, but need to ensure:
> - All queries filter by `IsDeleted = 0`
> - Indexes include WHERE clause for performance
> - Consider scheduled cleanup for old soft-deleted records

**Resolution**: **APPROVED** - Soft deletes with filtered indexes

---

##### Topic 5: AzureTag Uniqueness & Case Sensitivity

**Database Engineer Position**:
> AzureTag stored lowercase with UNIQUE constraint. Case-insensitive searches via LOWER() function or collation.

**Backend Lead Position**:
> Need to enforce lowercase at application level before insert:
```csharp
user.AzureTag = request.AzureTag.ToLowerInvariant();
```

**Resolution**: **APPROVED** - Store lowercase, enforce in application

---

##### Topic 6: Transaction Linking for Transfers

**Database Engineer Position**:
> External transfers create TWO transaction records:
> - `transfer_out` on sender's account
> - `transfer_in` on recipient's account
> - Linked via `RelatedTransactionId`

**Backend Lead Position**:
> This is the correct approach because:
> - Each account has complete history
> - Balance calculations are account-scoped
> - Audit trail is complete

**Concern**: What if one insert succeeds but the other fails?

**Resolution**: **APPROVED** - Both inserts must be in same database transaction. Rollback on any failure.

---

##### Topic 7: Historical Balance Query Performance

**Database Engineer Position**:
> `GetBalanceAtTime()` function scans transactions up to the specified time. For accounts with many transactions, this could be slow.

**Backend Lead Position**:
> Options considered:
> 1. Accept the query cost (accounts rarely have >10K transactions)
> 2. Create balance snapshots table (monthly/weekly snapshots)
> 3. Use covering index on `(AccountId, CreatedAt DESC) INCLUDE (BalanceAfter)`

**Resolution**: **APPROVED** - Option 3 (covering index) for MVP. Option 2 for future if needed.

---

##### Topic 8: RefreshTokens Table Necessity

**Database Engineer Position**:
> RefreshTokens table is marked as optional. Do we need it for MVP?

**Backend Lead Position**:
> For MVP:
> - Access tokens with 30-minute expiry
> - No refresh tokens (user re-logs on expiry)
> - Table exists for future implementation

**Resolution**: **DEFERRED** - Keep table structure, implement refresh tokens post-MVP

---

#### Action Items from Confrontation

| # | Action | Owner | Status |
|---|--------|-------|--------|
| 1 | Add `RowVersion` column for optimistic concurrency | Backend Lead | TODO |
| 2 | Document transaction isolation requirements | Backend Lead | TODO |
| 3 | Add covering index for balance history | Database Engineer | DONE |
| 4 | Create EF Core entity configurations | Backend Lead | TODO |
| 5 | Test seed data with actual SQL Server | Database Engineer | TODO |

---

#### Final Assessment

| Criteria | Status |
|----------|--------|
| Schema completeness | APPROVED |
| Relationship design | APPROVED |
| Index strategy | APPROVED |
| Constraint design | APPROVED |
| Security considerations | APPROVED |
| Performance considerations | APPROVED with action items |

**Confrontation Outcome**: Schema APPROVED for implementation

---

---

## 6. Phase 4 Notes

### 6.1 Backend Lead <-> Frontend Lead Confrontation

**Date**: 2026-01-08
**Participants**: Backend Lead, Frontend Lead
**Status**: COMPLETE

---

#### Discussion Topics & Resolutions

##### Topic 1: Money Format (Cents vs Decimal)

**Backend Lead Position**:
> API returns money as decimal (e.g., `1234.56`), not cents (e.g., `123456`). This is more intuitive for display and avoids frontend conversion errors.

**Frontend Lead Position**:
> Agree with decimal format. However, need to ensure:
> - Backend returns exactly 2 decimal places for display
> - Frontend formats with `Intl.NumberFormat` for locale-aware display
> - All calculations use proper decimal handling (no floating-point errors)

**Resolution**: **APPROVED** - Use decimal format with these rules:
1. API always returns decimal values (e.g., `1234.56`)
2. Frontend uses `Intl.NumberFormat('de-DE', { style: 'currency', currency: 'EUR' })` for display
3. Input validation ensures max 2 decimal places

---

##### Topic 2: Date Format

**Backend Lead Position**:
> All dates in ISO 8601 format: `2026-01-08T14:30:00Z` (UTC)

**Frontend Lead Position**:
> Frontend will:
> - Store dates as ISO strings
> - Display using `Intl.DateTimeFormat` for localization
> - Use `date-fns` for date calculations if needed

**Resolution**: **APPROVED** - ISO 8601 UTC format

---

##### Topic 3: Pagination Strategy

**Backend Lead Position**:
> Page-based pagination with `page` (1-based) and `pageSize` (default 20, max 100).
> Response includes `pagination` object with `totalItems`, `totalPages`, `hasNextPage`, `hasPreviousPage`.

**Frontend Lead Position**:
> RTK Query will cache paginated results. Need to consider:
> - Cache invalidation when new transactions are created
> - Infinite scroll vs. page navigation
> - Initial load should show most recent first (desc order)

**Concern**: Should we use cursor-based pagination for better performance on large datasets?

**Resolution**: **APPROVED** - Page-based for MVP with these notes:
1. Default sort: `createdAt DESC`
2. Frontend uses "Load More" button (not infinite scroll) to keep UX simple
3. Cache invalidation: RTK Query tags on transaction mutations
4. Consider cursor-based for v2 if needed

---

##### Topic 4: Error Response Structure

**Backend Lead Position**:
> Standard error format:
```json
{
  "type": "ERROR_CODE",
  "message": "Human-readable message",
  "correlationId": "uuid",
  "statusCode": 400,
  "errors": { "field": ["error"] },
  "details": { "available": 1000 }
}
```

**Frontend Lead Position**:
> This structure works well for RTK Query error handling:
> - `message` for toast notifications
> - `errors` for inline form validation
> - `type` for specific error handling (e.g., redirect on `UNAUTHORIZED`)
> - `correlationId` for support tickets

**Resolution**: **APPROVED** - Structure supports all frontend use cases

---

##### Topic 5: Response Wrapper

**Backend Lead Position**:
> All successful responses wrapped in `{ data: ..., message?: string }`

**Frontend Lead Position**:
> This adds consistency but requires unwrapping. RTK Query `transformResponse` can handle this.

**Concern**: Is the wrapper necessary, or should we return data directly?

**Resolution**: **APPROVED** - Keep wrapper for consistency:
1. Helps distinguish success from error responses
2. `message` useful for toast notifications
3. Frontend uses `transformResponse` to extract `data`

```typescript
transformResponse: (response: ApiResponse<T>) => response.data
```

---

##### Topic 6: AzureTag Search Behavior

**Backend Lead Position**:
> `GET /api/users/search?azureTag=jane` returns max 10 results with partial match.
> Display name is masked: "Jane D." (first name + last initial)

**Frontend Lead Position**:
> Need debouncing on search input (300ms). What's the minimum search length?

**Resolution**: **APPROVED** with specifications:
1. Minimum 2 characters to trigger search
2. Frontend debounces by 300ms
3. Returns max 10 results
4. Excludes current user from results
5. Case-insensitive search

---

##### Topic 7: Token Storage & Expiration

**Backend Lead Position**:
> Access token expires in 15 minutes. Session has 30 min inactivity timeout, 60 min absolute timeout.

**Frontend Lead Position**:
> ~~Token stored in Redux state (memory only).~~ **SUPERSEDED BY BFF PATTERN**

**Resolution**: **SUPERSEDED** - See Phase 5 Security Design

> **IMPORTANT UPDATE (Phase 5)**: This topic was re-evaluated and the decision was changed to use the **BFF (Backend-for-Frontend) Pattern** for maximum security.

**Updated Resolution** (BFF Pattern - ADR-021):
1. **Tokens are NEVER stored in the browser** (not Redux, not memory, not localStorage)
2. JWT is stored server-side in BFF session
3. Browser receives only HTTP-only session cookie
4. On 401, redirect to `/login`
5. Session timeout warning can use `/bff/auth/session-status` endpoint

See:
- [08-security-design.md](08-security-design.md) - Full security architecture
- [03-tech-stack-decisions.md](03-tech-stack-decisions.md) - ADR-021 BFF Pattern

---

##### Topic 8: Internal vs External Transfers

**Backend Lead Position**:
> Two separate endpoints:
> - `POST /api/transfers` - External (to another user via AzureTag)
> - `POST /api/transfers/internal` - Between own accounts

**Frontend Lead Position**:
> UI design shows single "Transfer" page with toggle. Is this a problem?

**Resolution**: **APPROVED** - Frontend determines endpoint based on UI state:
1. If "Send to User" tab: use `POST /api/transfers`
2. If "Between Accounts" tab: use `POST /api/transfers/internal`
3. Both endpoints return similar response structure for consistency

---

##### Topic 9: Loading States

**Backend Lead Position**:
> No specific guidance, handled by frontend.

**Frontend Lead Position**:
> RTK Query provides `isLoading`, `isFetching`, `isError` states. Need to define:
> - Skeleton loaders for initial load
> - Spinner for form submissions
> - Optimistic updates for better UX

**Resolution**: **APPROVED** - Frontend implementation:
1. Skeletons for page/list loading
2. Button spinners for form submissions
3. Optimistic updates for deposit/withdraw (show new balance immediately, revert on error)

---

##### Topic 10: Retry Strategy

**Backend Lead Position**:
> Rate limiting implemented. 429 response includes `Retry-After` header.

**Frontend Lead Position**:
> RTK Query can auto-retry failed requests. Should we configure this?

**Resolution**: **APPROVED** - Configure RTK Query retry:
1. Auto-retry on 5xx errors (max 3 attempts with exponential backoff)
2. No retry on 4xx (user errors)
3. Respect `Retry-After` header for 429

---

#### Action Items from Confrontation

| # | Action | Owner | Status |
|---|--------|-------|--------|
| 1 | Add `transformResponse` to RTK Query endpoints | Frontend Lead | TODO |
| 2 | Configure RTK Query retry policy | Frontend Lead | TODO |
| 3 | Implement debounced user search | Frontend Lead | TODO |
| 4 | Document 401 handling in RTK Query | Frontend Lead | TODO |
| 5 | Update MSW handlers to match API contracts | Frontend Lead | TODO |
| 6 | Ensure all money values have 2 decimal places | Backend Lead | TODO |

---

#### Final Assessment

| Criteria | Status |
|----------|--------|
| Request/Response formats | APPROVED |
| Error handling | APPROVED |
| Pagination strategy | APPROVED |
| Authentication flow | APPROVED |
| Data formats (dates, money) | APPROVED |
| Cache invalidation | APPROVED |

**Confrontation Outcome**: API Contract APPROVED for implementation

---

## 7. Phase 5 Notes

### 7.1 Security Architecture Decision - RESOLVED

**Date**: 2026-01-08
**Participants**: Security Specialist, Backend Lead, Frontend Lead
**Status**: COMPLETE

---

#### Security Decision: BFF Pattern for Authentication

##### Previous Approach (INCORRECT)
The Phase 4 confrontation noted "Token stored in Redux state (memory only)". This approach has security vulnerabilities:
- XSS attacks can steal tokens from JavaScript memory
- Token lost on page refresh (poor UX)
- Token visible in browser DevTools

##### Updated Decision: BFF Pattern (ADR-021)

**Architecture**:
```
Browser  -->  BFF (.NET + YARP)  -->  Backend API
   |                |                    |
   |  Session       |  JWT stored        |  JWT Bearer
   |  Cookie        |  server-side       |  token
   |  (HTTP-only)   |  (Memory/Redis)    |
```

**Key Points**:
1. **Tokens NEVER reach the browser**
2. Browser receives only session cookie (HTTP-only, Secure, SameSite=Strict)
3. BFF stores JWT in server-side session
4. YARP reverse proxy adds Bearer token to API requests
5. Redux stores only user info (no tokens)

##### Rationale
| Threat | Mitigation |
|--------|------------|
| XSS Token Theft | Tokens not in browser - nothing to steal |
| CSRF | SameSite=Strict cookies |
| Session Hijacking | HTTP-only, Secure, short expiry |

##### Implementation Impact
- NEW: `AzureBank.Bff` project with YARP
- NEW: Session management middleware
- NEW: BFF auth endpoints (`/bff/auth/*`)
- UPDATED: Frontend uses session cookie, not Bearer token
- UPDATED: Redux stores user info only

##### References
- [08-security-design.md](08-security-design.md) - Full security architecture
- [03-tech-stack-decisions.md](03-tech-stack-decisions.md) - ADR-021
- [02-architecture-overview.md](02-architecture-overview.md) - Updated diagrams

**Resolution**: BFF Pattern APPROVED for implementation

---

### 7.2 Security Specialist <-> Backend Lead Confrontation
```
[TODO: Document detailed confrontation in Phase 5.4]
```

---

## 8. Phase 9 Notes

### 8.1 Full Team Review
```
[TODO: Document in Phase 9.1]
```

---

**Status**: This document will be updated throughout the project
