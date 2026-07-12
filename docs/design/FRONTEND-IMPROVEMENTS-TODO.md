# Frontend Improvements TODO
## AzureBank - Future Enhancements

**Document Version**: 2.0
**Created**: 2026-01-08
**Updated**: 2026-01-09
**Status**: PARTIALLY COMPLETE - Some items applied in design iterations
**Priority**: MIXED (See individual items)

---

## Overview

This document tracks frontend improvements and polish items identified during testing. Items are now marked as COMPLETE or PENDING based on design document revisions.

---

## Status Summary

| Item | Status | Applied In |
|------|--------|------------|
| 1. MSW Mock Handlers | ⏳ PENDING | Backend phase |
| 2. Privacy Fix | ✅ COMPLETE | 04l v2.0 |
| 3. Wizard State Cleanup | ✅ COMPLETE | 04l v4.1 |
| 4. Copy-to-Clipboard | ✅ COMPLETE | 04l v4.1 |
| 5. Global Error Middleware | ✅ COMPLETE | 04l v4.1 |
| 6. RTK Query Retry Logic | ✅ COMPLETE | 04l v4.1 |
| 7. UI Polish Items | ⏳ PENDING | Post-MVP |
| 8. Performance Optimizations | ⏳ PENDING | Post-MVP |

---

## 1. MSW Mock Handlers (Priority: HIGH when ready)

**Status**: ⏳ PENDING - Required for standalone frontend testing
**Impact**: Frontend cannot function standalone without backend

### Tasks
- [ ] Create `src/mocks/handlers.ts` with all API endpoints
- [ ] Create `src/mocks/browser.ts` for browser setup
- [ ] Create `src/mocks/data.ts` with mock data factories
- [ ] Integrate MSW in `main.tsx` for development mode
- [ ] Test all flows with mock data

### Endpoints to Mock (BFF Pattern)
```
# Auth (Session-based, NOT JWT)
POST /api/auth/register
POST /api/auth/login
POST /api/auth/logout
GET  /api/auth/session

# Accounts
GET  /api/accounts
POST /api/accounts
GET  /api/accounts/:id/balance

# Transactions
GET  /api/transactions
POST /api/transactions/deposit
POST /api/transactions/withdraw

# Transfers
POST /api/transfers
GET  /api/users/search?azureTag=@username
```

**Note**: MSW handlers should use `credentials: 'include'` pattern and NOT return JWT tokens to the frontend.

---

## 2. Privacy Fix - Gemini v4.1 (Priority: HIGH)

**Status**: ✅ COMPLETE
**Applied In**: 04l-frontend-design-final-v2.md (v2.0)
**Date Completed**: 2026-01-07

### What Was Fixed
- ✅ Removed recipient account selection UI from TransferPage
- ✅ Simplified to "Send to User" model
- ✅ Sender sees only @AzureTag and masked name ("John S.")
- ✅ Transfer request does not include `toAccountNumber`
- ✅ Recipient's accounts are never exposed

### Verification
See `04l-frontend-design-final-v2.md` Section 2.2 - Transfer flow now uses:
- Recipient lookup by AzureTag only
- Display: masked name + @AzureTag
- Backend selects recipient's primary account

---

## 3. Wizard State Cleanup (Priority: MEDIUM)

**Status**: ✅ COMPLETE
**Applied In**: 04l-frontend-design-final-v2.md (v4.1)
**Date Completed**: 2026-01-08

### What Was Fixed
- ✅ Added `resetWizard` action to transferWizardSlice
- ✅ useEffect cleanup on component unmount
- ✅ Reset on modal close (X button, outside click)
- ✅ Reset after successful transfer
- ✅ Handles stale state scenarios

### Verification
See `09-redux-architecture.md` Section 4.2 - transferWizardSlice includes:
- `resetWizard` reducer
- Cleanup patterns documented

---

## 4. Copy-to-Clipboard Feature (Priority: LOW)

**Status**: ✅ COMPLETE
**Applied In**: 04l-frontend-design-final-v2.md (v4.1)
**Date Completed**: 2026-01-08

### What Was Fixed
- ✅ CopyButton utility component designed
- ✅ Copy button on AzureTag display
- ✅ Copy button on Account Number display
- ✅ Toast confirmation on copy
- ✅ Uses FluentUI `CopyRegular` icon

### Verification
See `04l-frontend-design-final-v2.md` - CopyButton component specification included.

---

## 5. Global Error Middleware (Priority: MEDIUM)

**Status**: ✅ COMPLETE
**Applied In**: 04l-frontend-design-final-v2.md (v4.1)
**Date Completed**: 2026-01-08

### What Was Fixed
- ✅ Error middleware for RTK Query
- ✅ 401 Unauthorized → Redirect to login (session expired)
- ✅ 500 Internal Server Error → Global toast notification
- ✅ Error handling across all endpoints

### Verification
See `09-redux-architecture.md` Section 5 - Error handling middleware documented.

**Note**: With BFF pattern, 401 means session expired (no token to clear).

---

## 6. RTK Query Retry Logic (Priority: LOW)

**Status**: ✅ COMPLETE
**Applied In**: 04l-frontend-design-final-v2.md (v4.1)
**Date Completed**: 2026-01-08

### What Was Fixed
- ✅ Retry configuration in baseQuery
- ✅ 3 retries with exponential backoff
- ✅ Only retry idempotent GET requests
- ✅ POST/PUT/DELETE excluded from retry

### Verification
See `09-redux-architecture.md` Section 2.2 - RTK Query configuration includes retry logic.

---

## 7. UI Polish Items (Priority: LOW)

**Status**: ⏳ PENDING - Post-MVP
**Target Phase**: Final QA

### Visual Improvements
- [ ] Review skeleton loading states
- [ ] Add success animations (Gemini recommendation)
- [ ] Verify mobile responsive layouts
- [ ] Test dark mode (if implemented)
- [ ] Review form validation UX

### Accessibility
- [ ] Verify WCAG 2.1 AA compliance
- [ ] Test keyboard navigation
- [ ] Test screen reader compatibility
- [ ] Verify focus indicators
- [ ] Check color contrast ratios

---

## 8. Performance Optimizations (Priority: LOW)

**Status**: ⏳ PENDING - Post-MVP
**Target Phase**: Final QA

### Tasks
- [ ] Review bundle size
- [ ] Add code splitting if needed
- [ ] Optimize images/assets
- [ ] Review re-render patterns
- [ ] Add React.memo where beneficial

---

## When to Address Remaining Items

| Phase | Items |
|-------|-------|
| After Backend API | 1. MSW Mock Handlers (for integration testing) |
| Final QA | 7. UI Polish, Accessibility |
| Post-Launch | 8. Performance Optimizations |

---

## Critical Update Required

**⚠️ ARCHITECTURE ALIGNMENT NEEDED**

The `frontend-design/` folder documents still reference the old JWT-in-browser pattern. These need updating to reflect BFF architecture:

- See `FRONTEND-DESIGN-UPDATE-PLAN.md` for detailed update steps
- Critical files: `04j`, `04e`, `04h`, `04g`
- Must update before implementation phase

---

## Notes

- Frontend from claude-code-figma team is a **solid foundation**
- Core functionality is working
- Most Gemini recommendations have been incorporated
- Backend should be priority now
- BFF pattern adoption requires documentation alignment

---

**Document Status**: ACTIVE - 6 of 8 items complete
**Next Review**: After backend API complete
**Related**: `FRONTEND-DESIGN-UPDATE-PLAN.md`

