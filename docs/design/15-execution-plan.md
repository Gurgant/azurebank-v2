# Comprehensive Execution Plan
## Bank Account Management System - Dev4Side Technical Test

**Document Version**: 1.0
**Created**: 2025-12-16
**Author**: System Architect
**Status**: APPROVED FOR EXECUTION

---

## Executive Summary

This document provides the complete, detailed execution plan for building a full-stack banking application. The plan follows a **design-first, documentation-driven methodology** with multi-AI team collaboration (Claude internal team + Gemini external review).

### Key Principles
1. **Measure twice, cut once** - Complete design before implementation
2. **Frontend-first development** - Build UI with mocks, connect backend later
3. **Cross-AI validation** - External review for quality assurance
4. **Human approval gates** - Checkpoints before major transitions

---

## Phase-by-Phase Execution Guide

---

## PHASE 0: INITIALIZATION [COMPLETED]

### Objectives
- Establish project structure
- Validate all requirements and constraints
- Set up documentation framework
- Define cross-AI collaboration protocol

### Tasks Completed
| Task | Status | Output |
|------|--------|--------|
| 0.1 Validate constraints | DONE | 01-constraints-validation.md |
| 0.2 Create file structure | DONE | 26 documentation files |
| 0.3 Initialize PROGRESS.md | DONE | PROGRESS.md |
| 0.4 Document cross-AI protocol | DONE | 14-cross-ai-protocol.md |

### Best Practices Applied
- Single source of truth for all constraints
- Clear file naming conventions (numbered, descriptive)
- Separation of concerns in documentation

---

## PHASE 1: ARCHITECTURE DESIGN

### Objectives
- Define high-level system architecture
- Finalize technology stack with justifications
- Design project folder structures
- Create Architecture Decision Records (ADRs)
- Design Redux state architecture

### Detailed Task Instructions

#### 1.1 High-Level System Architecture
**Input**: Constraints from 01-constraints-validation.md
**Output**: Update 02-architecture-overview.md

**Steps**:
1. Draw system context diagram (Users -> Frontend -> Backend -> Database)
2. Define layer responsibilities (Presentation, API, Business, Data)
3. Document data flow for key operations (auth, transactions)
4. Identify integration points

**Deliverables**:
- System context diagram
- Layer diagram
- Data flow diagrams
- Component interaction diagram

#### 1.2 Technology Stack Finalization
**Input**: ADRs from 03-tech-stack-decisions.md
**Output**: Finalize 03-tech-stack-decisions.md

**Steps**:
1. Review each ADR for completeness
2. Add version numbers where missing
3. Document any risks or trade-offs
4. Create dependency matrix

**Deliverables**:
- Finalized ADR for each technology choice
- Version matrix
- Dependency diagram

#### 1.3 Project Folder Structure
**Output**: Add to 02-architecture-overview.md

**Frontend Structure**:
```
frontend/
├── public/
├── src/
│   ├── app/              # Store, router, hooks
│   ├── features/         # Feature modules
│   │   ├── auth/
│   │   ├── account/
│   │   ├── transactions/
│   │   └── transfer/
│   ├── components/       # Shared components
│   │   ├── common/
│   │   ├── layout/
│   │   └── forms/
│   ├── pages/           # Route components
│   ├── hooks/           # Custom hooks
│   ├── types/           # TypeScript types
│   ├── utils/           # Utilities
│   ├── mocks/           # MSW handlers
│   └── theme/           # FluentUI theme
├── package.json
└── tsconfig.json
```

**Backend Structure**:
```
backend/
├── BankApp.API/
│   ├── Controllers/
│   ├── Services/
│   ├── Models/
│   │   ├── Entities/
│   │   ├── DTOs/
│   │   └── Enums/
│   ├── Data/
│   ├── Middleware/
│   ├── Extensions/
│   └── Program.cs
├── BankApp.Tests/
└── BankApp.sln
```

#### 1.4 Architecture Decision Records
**Output**: Finalize 03-tech-stack-decisions.md

**Required ADRs**:
1. Frontend Framework (React 19)
2. UI Component Library (FluentUI v9)
3. State Management (Redux Toolkit + RTK Query)
4. Backend Framework (.NET 10)
5. Database (SQL Server)
6. ORM (Entity Framework Core)
7. Authentication (JWT)
8. Password Hashing (Argon2id)
9. API Mocking (MSW)
10. Responsive Design Strategy
11. Project Organization

#### 1.5 Redux State Architecture
**Output**: Add to 09-redux-architecture.md (preliminary)

**State Shape**:
```typescript
interface RootState {
  auth: {
    token: string | null;
    user: User | null;
    isAuthenticated: boolean;
  };
  // RTK Query managed state
  [accountApi.reducerPath]: AccountApiState;
  [transactionApi.reducerPath]: TransactionApiState;
  [transferApi.reducerPath]: TransferApiState;
}
```

### Checkpoint
**Approval Required**: Human must approve Phase 1 before proceeding to Phase 2

---

## PHASE 2: FRONTEND DESIGN

### Objectives
- Create comprehensive UX/UI specifications
- Design visual identity with FluentUI
- Plan component architecture
- Validate with external review (Gemini)
- Produce final approved design

### Sub-Phase 2.1: UX/UI Expert Work

#### 2.1.1 Identify Required Views
**Output**: 04a-ux-user-flows.md

**Views to Define**:
| View | Route | Type | Priority |
|------|-------|------|----------|
| Login | /login | Public | P0 |
| Register | /register | Public | P0 |
| Dashboard | / | Protected | P0 |
| Deposit | /deposit or Modal | Protected | P0 |
| Withdraw | /withdraw or Modal | Protected | P0 |
| Transfer | /transfer or Modal | Protected | P0 |
| History | /history | Protected | P1 |
| Profile | /profile | Protected | P2 |

#### 2.1.2 Create User Flow Diagrams
**Output**: 04a-ux-user-flows.md

**Flows to Document**:
1. **Registration Flow**
   - Navigate to register
   - Enter details (email, password, confirm)
   - Submit
   - Success -> Auto-login -> Dashboard
   - Error -> Show validation errors

2. **Login Flow**
   - Navigate to login
   - Enter credentials
   - Submit
   - Success -> Dashboard
   - Error -> Show error message

3. **Deposit Flow**
   - From Dashboard, click Deposit
   - Enter amount
   - Confirm
   - Success -> Update balance, show confirmation
   - Error -> Show error

4. **Withdrawal Flow**
   - From Dashboard, click Withdraw
   - Enter amount
   - Validate against balance
   - Confirm
   - Success/Error handling

5. **Transfer Flow**
   - From Dashboard, click Transfer
   - Enter recipient account number
   - Enter amount
   - Validate
   - Confirm
   - Success/Error handling

#### 2.1.3 Design Wireframes
**Output**: 04b-ux-wireframes.md

**Wireframe for Each View**:
- Use ASCII art or detailed descriptions
- Show component placement
- Indicate responsive breakpoints
- Mark interactive elements

#### 2.1.4 Define Interaction Patterns
**Output**: 04a-ux-user-flows.md

**Patterns to Define**:
- Button states (hover, active, disabled)
- Form validation (inline, on-submit)
- Loading indicators
- Success/Error feedback
- Navigation transitions
- Modal behavior

#### 2.1.5 Mobile vs Desktop UX
**Output**: 04b-ux-wireframes.md

**Considerations**:
- Touch-friendly tap targets (min 44x44px)
- Simplified navigation on mobile
- Responsive form layouts
- Transaction history: cards vs table
- Bottom navigation vs sidebar

#### 2.1.6 Accessibility (WCAG 2.1 AA)
**Output**: 04b-ux-wireframes.md

**Requirements**:
- Keyboard navigation
- Screen reader compatibility
- Color contrast ratios (4.5:1 text, 3:1 UI)
- Focus indicators
- Form labels and ARIA
- Error announcements

#### 2.1.7 Error State Designs
**Output**: 04a-ux-user-flows.md

**Error States**:
- Form validation errors
- Network errors
- Server errors (500)
- Not found (404)
- Unauthorized (401)
- Insufficient funds
- Invalid account number

#### 2.1.8 Loading State Designs
**Output**: 04a-ux-user-flows.md

**Loading States**:
- Initial page load (skeleton)
- Form submission (button spinner)
- Data fetching (inline spinner)
- Full page loading

### Sub-Phase 2.2: Web Designer Work

#### 2.2.1 Color Palette
**Output**: 04d-design-tokens.md

**FluentUI Token Mapping**:
```
Primary: colorBrandBackground (banking blue)
Secondary: colorNeutralBackground1
Success: colorPaletteGreenBackground3
Error: colorPaletteRedBackground3
Warning: colorPaletteYellowBackground3
Text: colorNeutralForeground1
```

#### 2.2.2 Typography Scale
**Output**: 04d-design-tokens.md

**Using FluentUI Typography**:
```
Title1 (Semibold, 28px) - Page titles
Title2 (Semibold, 24px) - Section headers
Title3 (Semibold, 20px) - Card titles
Subtitle1 (Semibold, 16px) - Subheadings
Body1 (Regular, 14px) - Body text
Caption1 (Regular, 12px) - Helper text
```

#### 2.2.3 Spacing System
**Output**: 04d-design-tokens.md

**FluentUI Spacing**:
```
spacingHorizontalXXS: 2px
spacingHorizontalXS: 4px
spacingHorizontalS: 8px
spacingHorizontalM: 12px
spacingHorizontalL: 16px
spacingHorizontalXL: 20px
spacingHorizontalXXL: 24px
```

#### 2.2.4 Component Visual Specs
**Output**: 04c-design-visual-specs.md

**For Each Component**:
- FluentUI component name
- Customizations (if any)
- Size variants
- State styles
- Spacing

#### 2.2.5 Responsive Breakpoints
**Output**: 04d-design-tokens.md

**Breakpoints**:
```
Mobile: 0-479px
Tablet: 480-1023px
Desktop: 1024px+
```

#### 2.2.6 Icon Selection
**Output**: 04d-design-tokens.md

**FluentUI Icons**:
```
@fluentui/react-icons
- PersonRegular (user)
- WalletRegular (account)
- ArrowDownloadRegular (deposit)
- ArrowUploadRegular (withdraw)
- ArrowSwapRegular (transfer)
- HistoryRegular (history)
- SettingsRegular (settings)
- SignOutRegular (logout)
```

#### 2.2.7 Theme Configuration
**Output**: 04d-design-tokens.md

**Light/Dark Theme**:
```typescript
import { webLightTheme, webDarkTheme } from '@fluentui/react-components';

// Custom brand colors
const customBrand = {
  10: '#001E3C',  // Darkest
  // ... to 160
};
```

### Sub-Phase 2.3: Frontend Lead Work

#### 2.3.1 Component Hierarchy
**Output**: 04e-frontend-components.md

**Component Tree**:
```
App
├── FluentProvider (theme)
├── Provider (Redux store)
├── BrowserRouter
│   ├── AuthLayout
│   │   ├── LoginPage
│   │   └── RegisterPage
│   └── ProtectedLayout
│       ├── Sidebar/Navigation
│       ├── DashboardPage
│       │   ├── BalanceCard
│       │   ├── QuickActions
│       │   └── RecentTransactions
│       ├── DepositPage/Modal
│       ├── WithdrawPage/Modal
│       ├── TransferPage/Modal
│       └── HistoryPage
```

#### 2.3.2 Technical Feasibility
**Output**: 04e-frontend-components.md

**Assessment Areas**:
- FluentUI component availability
- Custom component needs
- Performance considerations
- Bundle size impact
- Browser compatibility

#### 2.3.3 RTK Query Endpoint Mapping
**Output**: 04e-frontend-components.md

**Endpoints**:
```typescript
// authApi
- login: mutation
- register: mutation

// accountApi
- getAccount: query
- getBalance: query
- getBalanceAtTime: query
- createAccount: mutation

// transactionApi
- getTransactions: query
- deposit: mutation
- withdraw: mutation

// transferApi
- transfer: mutation
```

#### 2.3.4 Custom Hooks
**Output**: 04e-frontend-components.md

**Hooks to Create**:
```typescript
useAuth() - Authentication state and actions
useAccount() - Current account data
useTransactions(filter) - Transaction history
useBalance() - Current balance
useResponsive() - Breakpoint detection
```

#### 2.3.5 TypeScript Interfaces
**Output**: 04e-frontend-components.md

**Core Interfaces**:
```typescript
interface User {
  id: string;
  email: string;
  firstName: string;
  lastName: string;
}

interface Account {
  id: string;
  accountNumber: string;
  balance: number;
  createdAt: string;
}

interface Transaction {
  id: string;
  type: 'deposit' | 'withdrawal' | 'transfer_in' | 'transfer_out';
  amount: number;
  description: string;
  createdAt: string;
  balanceAfter: number;
}
```

#### 2.3.6 FluentUI Component Mapping
**Output**: 04e-frontend-components.md

**Mapping Table**:
| UI Element | FluentUI Component |
|------------|-------------------|
| Primary Button | Button (appearance="primary") |
| Text Input | Input |
| Password Input | Input (type="password") |
| Card | Card |
| Data Table | Table / DataGrid |
| Modal | Dialog |
| Toast | Toast |
| Spinner | Spinner |
| Navigation | TabList or custom |

### Sub-Phase 2.4: Internal Team Consolidation

#### 2.4.1 UX/UI Expert <-> Web Designer Confrontation
**Output**: 04f-frontend-design-report-v1.md

**Discussion Topics**:
- Visual hierarchy alignment
- Spacing consistency
- Color usage for states
- Mobile layout decisions
- Animation/transition preferences

#### 2.4.2 Designer Team <-> Frontend Lead Confrontation
**Output**: 04f-frontend-design-report-v1.md

**Discussion Topics**:
- FluentUI component limitations
- Custom component needs
- Performance vs visual richness
- Accessibility implementation
- Responsive breakpoint handling

#### 2.4.3 Produce Frontend Design Report v1
**Output**: 04f-frontend-design-report-v1.md

**Report Structure**:
1. Executive Summary
2. UX Findings
3. Visual Design Specs
4. Technical Assessment
5. Confrontation Outcomes
6. Open Questions

### Sub-Phase 2.5: External Review (Gemini)

#### 2.5.1 Prepare Gemini Prompt
**Output**: 04g-gemini-review-prompt.md

**Include**:
- Original requirements
- Complete Design Report v1
- Specific questions for review
- Output format requirements

#### 2.5.2 Gemini Handoff
**Action**: Human executes prompt in Gemini

#### 2.5.3 Await Response
**Status**: BLOCKED until human provides response

#### 2.5.4 Document Response
**Output**: 04h-gemini-review-response.md

### Sub-Phase 2.6: Cross-AI Resolution

#### 2.6.1 Claude vs Gemini Confrontation
**Output**: 04i-cross-ai-confrontation.md

For each Gemini recommendation:
1. State Gemini position
2. State Claude position
3. Analyze trade-offs
4. Reach resolution

#### 2.6.2-2.6.3 Resolve and Merge
**Output**: 04i-cross-ai-confrontation.md

#### 2.6.4 Produce Final Design
**Output**: 04j-frontend-design-final.md

### Sub-Phase 2.7: Individual Responsibility Evaluation

#### 2.7.1-2.7.3 Final Specifications
**Output**: Update respective documents

### Checkpoint
**Approval Required**: Human must approve Phase 2 before proceeding

---

## PHASE 3: DATABASE DESIGN

### Objectives
- Define all database entities
- Create ERD
- Write SQL DDL scripts
- Plan indexing strategy

### Detailed Tasks

#### 3.1 Identify Entities
**Output**: 05-database-schema.md

**Entities**:
1. **Users**
   - Id (PK, GUID)
   - Email (unique)
   - PasswordHash
   - FirstName
   - LastName
   - CreatedAt
   - UpdatedAt

2. **Accounts**
   - Id (PK, GUID)
   - AccountNumber (unique, generated)
   - UserId (FK -> Users)
   - Balance (decimal)
   - CreatedAt
   - UpdatedAt

3. **Transactions**
   - Id (PK, GUID)
   - AccountId (FK -> Accounts)
   - Type (enum: Deposit, Withdrawal, TransferIn, TransferOut)
   - Amount (decimal)
   - BalanceAfter (decimal)
   - Description
   - RelatedTransactionId (FK -> Transactions, nullable)
   - CreatedAt

#### 3.2 Map Relationships
**Output**: 05-database-schema.md

```
Users 1:N Accounts
Accounts 1:N Transactions
Transactions 1:1 Transactions (for transfers)
```

#### 3.3 SQL DDL Scripts
**Output**: 05-database-schema.md

**Include**:
- CREATE TABLE statements
- Primary keys
- Foreign keys
- Unique constraints
- Check constraints
- Default values

#### 3.4 Database Engineer <-> Backend Lead Confrontation
**Output**: 13-review-notes.md

**Discussion Topics**:
- Normalization level
- Decimal precision for money
- Transaction history retention
- Balance calculation strategy

#### 3.5 Index Strategy
**Output**: 05-database-schema.md

**Indexes**:
- Users.Email (unique)
- Accounts.AccountNumber (unique)
- Accounts.UserId
- Transactions.AccountId
- Transactions.CreatedAt

### Checkpoint
**Approval Required**: Human must approve Phase 3

---

## PHASE 4: API DESIGN

### Objectives
- Define all API endpoints
- Create request/response DTOs
- Establish error handling conventions
- Design MSW mock handlers

### Detailed Tasks

#### 4.1 Endpoint Inventory
**Output**: 06-api-contracts.md

**Endpoints**:
```
Auth:
POST /api/auth/register
POST /api/auth/login

Accounts:
POST /api/accounts          (create account)
GET  /api/accounts          (get user's accounts)
GET  /api/accounts/{id}/balance
GET  /api/accounts/{id}/balance?at={datetime}

Transactions:
GET  /api/transactions?accountId={id}&from={date}&to={date}
POST /api/transactions/deposit
POST /api/transactions/withdraw

Transfers:
POST /api/transfers
```

#### 4.2 Define DTOs
**Output**: 06-api-contracts.md

**For Each Endpoint**:
- Request DTO (body, query params)
- Response DTO
- Error response format

#### 4.3 Error Handling
**Output**: 06-api-contracts.md

**Standard Error Response**:
```json
{
  "type": "error_type",
  "message": "Human readable message",
  "errors": {
    "field": ["error1", "error2"]
  }
}
```

**HTTP Status Codes**:
- 200: Success
- 201: Created
- 400: Bad Request
- 401: Unauthorized
- 403: Forbidden
- 404: Not Found
- 422: Validation Error
- 500: Server Error

#### 4.4 Backend <-> Frontend Confrontation
**Output**: 13-review-notes.md

**Topics**:
- Pagination strategy
- Date format (ISO 8601)
- Money format (cents vs decimal)
- Error response structure

#### 4.5 OpenAPI Specification
**Output**: 06-api-contracts.md (outline)

#### 4.6 MSW Mock Handlers
**Output**: 07-msw-mock-handlers.md

**For Each Endpoint**:
- Handler function
- Mock data
- Success response
- Error scenarios

### Checkpoint
**Approval Required**: Human must approve Phase 4

---

## PHASE 5: SECURITY DESIGN

### Objectives
- Define JWT strategy
- Plan password security
- Design authorization
- Create security checklist

### Detailed Tasks

#### 5.1 JWT Strategy
**Output**: 08-security-design.md

**Token Configuration**:
```
Access Token:
- Algorithm: HS256 or RS256
- Expiration: 15-30 minutes
- Claims: sub (userId), email, exp, iat
- Storage: Redux state (memory)

Refresh Token (if implemented):
- Expiration: 7 days
- Storage: HTTP-only cookie
- Rotation on use
```

#### 5.2 Password Hashing
**Output**: 08-security-design.md

**Argon2id Configuration**:
```
Memory: 65536 KB (64 MB)
Time: 3 iterations
Parallelism: 4
Salt: 16 bytes (random)
Hash length: 32 bytes
```

**Password Requirements**:
- Minimum 8 characters
- At least one uppercase
- At least one lowercase
- At least one number

#### 5.3 Authorization Middleware
**Output**: 08-security-design.md

**Middleware Flow**:
1. Extract token from Authorization header
2. Validate token signature
3. Check expiration
4. Extract claims
5. Set user context
6. Proceed or return 401

#### 5.4 Security Specialist <-> Backend Lead Confrontation
**Output**: 13-review-notes.md

**Topics**:
- Refresh token necessity
- Token revocation strategy
- Rate limiting
- CORS configuration

#### 5.5 Redux Auth Slice Security
**Output**: 08-security-design.md

**Considerations**:
- Token in memory only (not localStorage)
- Clear on logout
- Handle token expiration
- Automatic refresh (if implemented)

#### 5.6 Security Checklist
**Output**: 08-security-design.md

**Checklist**:
- [ ] HTTPS in production
- [ ] JWT signature validation
- [ ] Password hashing with Argon2id
- [ ] SQL injection prevention
- [ ] XSS prevention
- [ ] CSRF protection
- [ ] Rate limiting
- [ ] Input validation
- [ ] Error message sanitization
- [ ] Sensitive data logging prevention

### Checkpoint
**Approval Required**: Human must approve Phase 5

---

## PHASE 6: FRONTEND ARCHITECTURE (Implementation Ready)

### Objectives
- Finalize all frontend specifications
- Create implementation-ready documentation
- Configure all tools and libraries

### Detailed Tasks

#### 6.1 Folder Structure Finalization
**Output**: 10-implementation-guide-frontend.md

#### 6.2 Redux Store Configuration
**Output**: 09-redux-architecture.md (finalize)

**Include**:
- Store setup code
- Middleware configuration
- DevTools configuration
- Persist configuration (if any)

#### 6.3 RTK Query API Definitions
**Output**: 09-redux-architecture.md

**Include**:
- Base query configuration
- Endpoint definitions
- Tag invalidation strategy
- Error handling

#### 6.4 MSW Handlers Specification
**Output**: 07-msw-mock-handlers.md (finalize)

**Include**:
- Complete handler implementations
- Mock data factories
- Error simulation handlers
- Setup/teardown code

#### 6.5 FluentUI Theme Configuration
**Output**: 10-implementation-guide-frontend.md

**Include**:
- Theme token customizations
- Dark/light theme setup
- FluentProvider configuration

#### 6.6 Responsive Layout System
**Output**: 10-implementation-guide-frontend.md

**Include**:
- Layout components
- Responsive utilities
- Media query hooks

#### 6.7 Component Implementation Checklist
**Output**: 10-implementation-guide-frontend.md

**Checklist for Each Component**:
- [ ] Component created
- [ ] Props typed
- [ ] Styles applied
- [ ] Responsive behavior
- [ ] Loading states
- [ ] Error states
- [ ] Accessibility verified
- [ ] Unit tests (if required)

#### 6.8 Route Configuration
**Output**: 10-implementation-guide-frontend.md

**Include**:
- Route definitions
- Protected route wrapper
- Navigation configuration

### Checkpoint
**Approval Required**: Human must approve Phase 6

---

## PHASE 7: BACKEND ARCHITECTURE [COMPLETED]

### Objectives
- Finalize all backend specifications
- Create implementation-ready documentation

**Status**: COMPLETE (2026-01-08)

### Completed Tasks

| Task | Status | Output |
|------|--------|--------|
| 7.1 Solution Structure | DONE | CLI commands, NuGet packages, directory layout |
| 7.2 Shared Library | DONE | Entities, DTOs, Constants, Enums, Exceptions |
| 7.3 EF Core Configuration | DONE | DbContext, entity configs, migrations, seeder |
| 7.4 Backend API Structure | DONE | Controllers, Services, Validators, Mappers |
| 7.5 BFF Structure | DONE | YARP, session management, auth controller |
| 7.6 Middleware & DI | DONE | Global exception handler, correlation ID |
| 7.7 Implementation Checklist | DONE | Step-by-step verification list |

### Deliverables Created
- `11-implementation-guide-backend.md` - Comprehensive 4300+ line backend guide
  - Section 1-3: Prerequisites, Setup, Structure
  - Section 4: Shared Library (Entities, DTOs, Constants, Exceptions)
  - Section 5: EF Core Configuration (DbContext, Configurations, Seeder)
  - Section 6: Backend API (Services, Controllers, Validators, Mappers)
  - Section 7: BFF Gateway (Session, YARP, Security Headers)
  - Section 8: Middleware & Cross-Cutting Concerns
  - Section 9: Configuration Files
  - Section 10: Implementation Checklist

### Key Features Documented
- **Three-project architecture**: AzureBank.Shared, AzureBank.Api, AzureBank.Bff
- **BFF Pattern**: YARP reverse proxy with server-side session management
- **Security**: Argon2id password hashing, JWT, HTTP-only cookies
- **Observability**: Serilog logging, correlation IDs
- **Validation**: FluentValidation with detailed validators
- **Mapping**: Mapperly (compile-time, zero reflection)

### Checkpoint
**Status**: APPROVED (2026-01-08)

---

## PHASE 8: DOCUMENTATION

### Objectives
- Create comprehensive README
- Document setup procedures
- Document API

### Detailed Tasks

#### 8.1-8.5 Complete Documentation
**Output**: 12-readme-template.md (finalize) -> README.md

### Checkpoint
**Approval Required**: Human must approve Phase 8

---

## PHASE 9: FINAL REVIEW

### Objectives
- Full team review
- Completeness verification
- Risk assessment
- Final approval

### Detailed Tasks

#### 9.1 Full Team Review Session
**Participants**: All team members (simulated)

**Review Areas**:
- All documents complete
- All specifications consistent
- All decisions documented
- All risks identified

#### 9.2 Completeness Checklist
**Output**: PROGRESS.md

#### 9.3 Risk Assessment
**Output**: PROGRESS.md

#### 9.4 Final Approval
**Output**: PROGRESS.md

#### 9.5 ChatGPT Implementation Handoff Report
**Output**: 16-chatgpt-handoff-report.md (new)

---

## BEST PRACTICES TO ADOPT

### Documentation
1. **Single source of truth** - One document per topic
2. **Version control** - Track document versions
3. **Cross-references** - Link between related documents
4. **Status tracking** - Always know document completeness

### Design
1. **Mobile-first** - Design for smallest screen first
2. **Accessibility from start** - Don't retrofit
3. **Component reuse** - Design for reusability
4. **Consistency** - Follow design tokens

### Development
1. **Frontend-first** - Build UI with mocks
2. **Type safety** - TypeScript strict mode
3. **Error handling** - Plan for failures
4. **Testing mindset** - Design for testability

### Security
1. **Defense in depth** - Multiple security layers
2. **Least privilege** - Minimal permissions
3. **Secure defaults** - Safe by default
4. **Audit trail** - Log security events

### Process
1. **Phase gates** - Human approval checkpoints
2. **Cross-validation** - External review
3. **Documentation-driven** - Document before code
4. **Iterative refinement** - Improve continuously

---

## CHATGPT HANDOFF REPORT TEMPLATE

When Phase 9 is complete, generate a report for ChatGPT to continue implementation:

```markdown
# Implementation Handoff Report for ChatGPT

## Project Overview
[Summary of project and current state]

## Completed Design Artifacts
[List all completed documents with summaries]

## Technology Stack
[Final technology decisions]

## Implementation Priority
[Ordered list of implementation tasks]

## Key Design Decisions
[Critical decisions ChatGPT must follow]

## Open Questions
[Any unresolved items]

## Getting Started
[First steps for implementation]
```

---

---

## PHASE 10: FIGMA DESIGN HANDOFF (NEW)

### Objectives
- Generate comprehensive Figma design specifications
- Enable ClaudeTeam2Figma to recreate all UI designs
- Provide pixel-perfect specifications for implementation reference

### Prerequisites
- Phase 2 (Frontend Design) must be complete
- Gemini review must be complete
- All design documents must be at final version

### Detailed Tasks

#### 10.1 Prepare Design Export Package
**Output**: `project-docs/figma-handoff/00-figma-master-spec.md`

**Contents**:
- Complete color palette with hex values
- Typography scale with exact sizes
- Spacing system values
- Component inventory with dimensions
- All screen mockups in text form

#### 10.2 Create Figma Prompt
**Output**: `project-docs/figma-handoff/01-figma-prompt.md`

**Prompt Structure**:
1. Project context and brand identity
2. Design system tokens
3. Screen-by-screen specifications
4. Component library requirements
5. Responsive behavior rules

#### 10.3 Execute Figma Generation
**Action**: Human uses prompt with Claude (or Figma AI) to generate designs

**Expected Outputs**:
- Figma file with all screens
- Component library
- Design tokens configured
- Responsive variants

#### 10.4 Review and Iterate
**Output**: Updated Figma files

### Figma Handoff Document Structure

```
project-docs/figma-handoff/
├── 00-figma-master-spec.md     # Complete design spec
├── 01-figma-prompt.md          # Prompt for ClaudeTeam2Figma
├── 02-screen-inventory.md      # All screens to create
├── 03-component-library.md     # Reusable components
└── 04-design-tokens-figma.md   # Figma-specific token format
```

### Checkpoint
**Approval Required**: Human must approve Figma designs before implementation

---

## UPDATED PHASE FLOW

```
Phase 0: Initialization       [COMPLETE]
    ↓
Phase 1: Architecture Design  [COMPLETE]
    ↓
Phase 2: Frontend Design      [COMPLETE - v4.1]
    ↓
    ├─→ Phase 2.5: Gemini Review       [COMPLETE]
    │       ↓
    │   Phase 2.6: Cross-AI Resolution [COMPLETE]
    ↓
Phase 10: Figma Handoff       [COMPLETE - claude-code-figma delivered]
    ↓
    └─→ Frontend Implementation [COMPLETE - 27 HTML + React code]
        (Improvements tracked in FRONTEND-IMPROVEMENTS-TODO.md)
    ↓
Phase 3: Database Design      [COMPLETE]
    ↓
    └─→ Cross-Cutting Concerns Discussion [COMPLETE]
        (17-cross-cutting-concerns.md + ADR-017 to ADR-020)
    ↓
Phase 4: API Design           [COMPLETE]
    ↓
    └─→ 06-api-contracts.md    [COMPLETE - 19 endpoints + BFF endpoints]
    └─→ 07-msw-mock-handlers.md [COMPLETE - Updated for BFF]
    └─→ Backend/Frontend Confrontation [COMPLETE]
    ↓
Phase 5: Security Design      [COMPLETE]  ◄── JUST COMPLETED
    ↓
    └─→ 08-security-design.md  [COMPLETE - BFF Pattern, 15 sections]
    └─→ ADR-021 BFF Pattern    [COMPLETE]
    └─→ Architecture updated   [COMPLETE - BFF diagrams added]
    └─→ POST-MVP-ROADMAP.md    [CREATED - Future enhancements]
    ↓
Phase 6: Frontend Architecture [PENDING - partially done by Figma team]
    ↓
Phase 7: Backend Architecture  [PENDING]
    ↓
Phase 8: Documentation        [PENDING]
    ↓
Phase 9: Final Review         [PENDING]
```

---

**Document Status**: COMPLETE - Ready for Execution
**Current Phase**: Phase 5 (Security Design) - COMPLETE
**Next Step**: Phase 6 (Frontend Architecture) or Phase 7 (Backend Architecture)
