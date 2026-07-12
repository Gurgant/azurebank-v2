# AzureBank UI/UX Evaluation Master Plan

> **Document Purpose**: Multi-session plan for evaluating, designing, and implementing UI/UX pages and components
> **Created**: 2026-01-30
> **Stack**: React 19 + TypeScript + Fluent UI v9 + TanStack Router + Bun
> **Focus**: Design/View layer (data fetching abstracted via RTK Query hooks)

---

## Executive Summary

This document outlines a structured, multi-session approach to evaluate and implement the AzureBank web application UI/UX. Based on enterprise banking standards and modern React patterns, the plan follows an iterative cycle:

```
Planning → Validation → Gap Analysis → Corrections → Re-evaluation → Fixes
```

Each session is designed to be **self-contained** with clear deliverables, enabling pause-and-resume workflows.

---

## Research Foundation

### Enterprise Banking UI/UX Standards (2025-2026)

Based on comprehensive research from industry sources:

| Principle | Description | Source |
|-----------|-------------|--------|
| **Mobile-First** | Design for mobile, scale to desktop | [Banking App UX Best Practices](https://adamfard.com/blog/banking-app-ux) |
| **Progressive Disclosure** | Reveal complexity in layers | [Enterprise UX Design 2026](https://www.wearetenet.com/blog/enterprise-ux-design) |
| **Task-Based Navigation** | Organize by user tasks, not features | [Fintech UX Design Guide](https://www.eleken.co/blog-posts/modern-fintech-design-guide) |
| **Security-First UX** | Visible security cues build trust | [Fintech Design Patterns](https://phenomenonstudio.com/article/fintech-ux-design-patterns-that-build-trust-and-credibility/) |
| **WCAG 2.2 AA** | Accessibility as baseline requirement | [Accessible FinTech Design](https://ailleron.com/insights/how-to-design-accessible-fin-tech-and-bank-solutions/) |

### Fluent UI v9 Architecture Patterns

From [Fluent UI React v9 Documentation](https://react.fluentui.dev/):

- **Slots & Hooks Composition** - Predictable component structure
- **Griffel CSS-in-JS** - Type-safe, atomic CSS generation
- **Design Tokens** - Theme-aware styling via `tokens.colorBrandPrimary`
- **Tabster Integration** - Built-in keyboard navigation and focus management
- **Theme Provider** - Light/Dark/High-Contrast modes out of box

### Design Token Best Practices

From [UXPin Design Tokens Guide](https://www.uxpin.com/studio/blog/managing-global-styles-in-react-with-design-tokens/):

- **Token Hierarchy**: Primitive → Semantic → Component-specific
- **Naming Convention**: Purpose-based (not color-based)
- **TypeScript Integration**: Type-safe token access with IntelliSense
- **CSS Variables**: Instant theme switching without re-render

---

## AzureBank Feature Inventory

Based on backend API analysis from `CLAUDE-CONTEXT.md`:

### Core Features (API-Backed)

| Feature | API Endpoints | UI Requirements |
|---------|---------------|-----------------|
| **Authentication** | `/auth/login`, `/auth/register`, `/auth/logout` | Login, Register, Session management |
| **PIN Management** | `/auth/pin`, `/auth/pin/verify` | PIN setup, PIN verification modal |
| **Accounts** | `/accounts`, `/accounts/{id}`, `/accounts/{id}/full-number` | Account list, Account details, Full number reveal |
| **Transactions** | `/transactions`, `/transactions/{id}`, `/transactions/deposit`, `/transactions/withdraw` | Transaction list, Transaction detail, Deposit form, Withdraw form |
| **Transfers** | `/transfers`, `/transfers/internal` | External transfer, Internal transfer |
| **Users** | `/users/search`, `/users/{azureTag}` | Recipient search, Recipient lookup |

### Security Patterns (UI Impact)

| Pattern | Trigger | UI Component Needed |
|---------|---------|---------------------|
| **Step-Up Auth** | Withdrawal, External transfer | PIN verification modal |
| **Idempotency** | All mutations | Loading states, Retry prevention |
| **Session Expiry** | 30 min inactivity | Session warning, Auto-logout |

---

## Session Structure Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                        SESSION DEPENDENCY GRAPH                              │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│   SESSION 1: Page Inventory & Information Architecture                       │
│       ↓                                                                      │
│   SESSION 2: Component Taxonomy & Reusability Analysis                       │
│       ↓                                                                      │
│   SESSION 3: Design Token System & Theme Structure                           │
│       ↓                                                                      │
│   SESSION 4: Validation & Gap Analysis                                       │
│       ↓                                                                      │
│   SESSION 5: Plan Corrections & Refinement                                   │
│       ↓                                                                      │
│   SESSION 6: Re-evaluation & Final Verification                              │
│       ↓                                                                      │
│   SESSION 7: Fixes & Implementation Readiness                                │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## SESSION 1: Page Inventory & Information Architecture

### Objective
Define all pages/routes needed, their hierarchy, and navigation structure.

### Phase 1.1: Public Pages (Unauthenticated)

#### Step 1.1.1: Landing/Marketing Page
| Substep | Task | Deliverable |
|---------|------|-------------|
| 1.1.1.1 | Define hero section content | Hero component spec |
| 1.1.1.2 | Identify feature highlights | Feature cards spec |
| 1.1.1.3 | Design CTA placement | CTA button positioning |
| 1.1.1.4 | Plan trust indicators | Security badges, testimonials |

#### Step 1.1.2: Authentication Pages
| Substep | Task | Deliverable |
|---------|------|-------------|
| 1.1.2.1 | Login page layout | Login form component spec |
| 1.1.2.2 | Registration page layout | Multi-step register form spec |
| 1.1.2.3 | Password requirements display | Validation feedback component |
| 1.1.2.4 | Error state designs | Error message patterns |

### Phase 1.2: Authenticated Pages (Dashboard Shell)

#### Step 1.2.1: Navigation Structure
| Substep | Task | Deliverable |
|---------|------|-------------|
| 1.2.1.1 | Define primary nav items | Nav item list with icons |
| 1.2.1.2 | Design sidebar/header layout | Shell component spec |
| 1.2.1.3 | Plan mobile navigation | Responsive nav patterns |
| 1.2.1.4 | Define breadcrumb structure | Breadcrumb component spec |

#### Step 1.2.2: Dashboard Home
| Substep | Task | Deliverable |
|---------|------|-------------|
| 1.2.2.1 | Account summary cards | Card component spec |
| 1.2.2.2 | Recent transactions widget | Transaction list preview spec |
| 1.2.2.3 | Quick actions panel | Action button group spec |
| 1.2.2.4 | Welcome/greeting section | User context display spec |

### Phase 1.3: Feature Pages

#### Step 1.3.1: Accounts Section
| Substep | Task | Deliverable |
|---------|------|-------------|
| 1.3.1.1 | Accounts list page | Account card grid spec |
| 1.3.1.2 | Account detail page | Detail view layout spec |
| 1.3.1.3 | Full account number reveal | PIN-protected reveal flow |
| 1.3.1.4 | Create account flow | Account creation wizard spec |

#### Step 1.3.2: Transactions Section
| Substep | Task | Deliverable |
|---------|------|-------------|
| 1.3.2.1 | Transaction history page | Paginated list with filters |
| 1.3.2.2 | Transaction detail page | Detail view spec |
| 1.3.2.3 | Deposit flow | Deposit form + confirmation |
| 1.3.2.4 | Withdraw flow | Withdraw form + PIN step-up |

#### Step 1.3.3: Transfers Section
| Substep | Task | Deliverable |
|---------|------|-------------|
| 1.3.3.1 | Internal transfer page | Account-to-account form |
| 1.3.3.2 | External transfer page | Recipient search + form |
| 1.3.3.3 | Transfer confirmation | Confirmation modal spec |
| 1.3.3.4 | Transfer receipt | Success state with details |

### Phase 1.4: Settings & Profile

#### Step 1.4.1: User Settings
| Substep | Task | Deliverable |
|---------|------|-------------|
| 1.4.1.1 | Profile information page | Profile form spec |
| 1.4.1.2 | PIN management page | Set/Change PIN flow |
| 1.4.1.3 | Security settings | MFA, session management |
| 1.4.1.4 | Preferences page | Theme, notifications |

### Deliverables for Session 1
- [ ] Complete page inventory spreadsheet
- [ ] Route structure document (TanStack Router config)
- [ ] Navigation hierarchy diagram
- [ ] Page-to-API mapping matrix

---

## SESSION 2: Component Taxonomy & Reusability Analysis

### Objective
Categorize all UI components using Atomic Design methodology, identify reusable patterns.

### Phase 2.1: Atoms (Basic Building Blocks)

#### Step 2.1.1: Form Elements
| Substep | Task | Fluent UI Component |
|---------|------|---------------------|
| 2.1.1.1 | Text inputs | `Input`, `Field` |
| 2.1.1.2 | Select/dropdown | `Dropdown`, `Combobox` |
| 2.1.1.3 | Buttons (variants) | `Button` (primary, secondary, subtle) |
| 2.1.1.4 | Checkboxes/Radio | `Checkbox`, `Radio` |

#### Step 2.1.2: Display Elements
| Substep | Task | Fluent UI Component |
|---------|------|---------------------|
| 2.1.2.1 | Typography variants | `Text`, `Title`, `Subtitle` |
| 2.1.2.2 | Icons | `bundleIcon` from `@fluentui/react-icons` |
| 2.1.2.3 | Badges/Tags | `Badge`, `Tag` |
| 2.1.2.4 | Avatars | `Avatar`, `AvatarGroup` |

#### Step 2.1.3: Feedback Elements
| Substep | Task | Fluent UI Component |
|---------|------|---------------------|
| 2.1.3.1 | Loading indicators | `Spinner`, `ProgressBar` |
| 2.1.3.2 | Skeleton placeholders | `Skeleton` |
| 2.1.3.3 | Toast notifications | `Toast`, `Toaster` |
| 2.1.3.4 | Inline messages | `MessageBar` |

### Phase 2.2: Molecules (Simple Combinations)

#### Step 2.2.1: Form Molecules
| Substep | Task | Composition |
|---------|------|-------------|
| 2.2.1.1 | Labeled input with error | `Field` + `Input` + validation |
| 2.2.1.2 | Search input with icon | `Input` + `SearchRegular` icon |
| 2.2.1.3 | Password input with toggle | `Input` + visibility toggle |
| 2.2.1.4 | Amount input with currency | `Input` + currency prefix |

#### Step 2.2.2: Card Molecules
| Substep | Task | Composition |
|---------|------|-------------|
| 2.2.2.1 | Account card | `Card` + balance + account type |
| 2.2.2.2 | Transaction item | Icon + description + amount |
| 2.2.2.3 | Recipient card | `Avatar` + name + azureTag |
| 2.2.2.4 | Stat card | Label + value + trend indicator |

### Phase 2.3: Organisms (Complex Components)

#### Step 2.3.1: Page-Level Organisms
| Substep | Task | Description |
|---------|------|-------------|
| 2.3.1.1 | Header organism | Logo + nav + user menu |
| 2.3.1.2 | Sidebar organism | Nav links + collapse control |
| 2.3.1.3 | Footer organism | Links + legal text |
| 2.3.1.4 | Data table organism | `DataGrid` with sorting/filtering |

#### Step 2.3.2: Feature Organisms
| Substep | Task | Description |
|---------|------|-------------|
| 2.3.2.1 | Transfer form organism | Amount + recipient + description + PIN |
| 2.3.2.2 | Transaction list organism | Filter bar + paginated list + empty state |
| 2.3.2.3 | Account overview organism | Balance cards + quick actions |
| 2.3.2.4 | PIN verification organism | PIN input + biometric option + error handling |

### Phase 2.4: Templates (Page Layouts)

#### Step 2.4.1: Layout Templates
| Substep | Task | Description |
|---------|------|-------------|
| 2.4.1.1 | Auth layout | Centered form, no shell |
| 2.4.1.2 | Dashboard layout | Header + sidebar + content area |
| 2.4.1.3 | Detail page layout | Breadcrumb + title + content + actions |
| 2.4.1.4 | Form page layout | Stepper header + form + navigation |

### Deliverables for Session 2
- [ ] Component inventory spreadsheet (categorized by atomic level)
- [ ] Fluent UI component mapping document
- [ ] Custom component requirements list
- [ ] Component dependency graph

---

## SESSION 3: Design Token System & Theme Structure

### Objective
Define the design token hierarchy and theming approach for AzureBank.

### Phase 3.1: Token Inventory

#### Step 3.1.1: Color Tokens
| Substep | Token Category | Examples |
|---------|----------------|----------|
| 3.1.1.1 | Brand colors | `colorBrandPrimary`, `colorBrandSecondary` |
| 3.1.1.2 | Semantic colors | `colorSuccess`, `colorWarning`, `colorError` |
| 3.1.1.3 | Neutral palette | `colorNeutralBackground`, `colorNeutralForeground` |
| 3.1.1.4 | Banking-specific | `colorPositiveAmount`, `colorNegativeAmount` |

#### Step 3.1.2: Typography Tokens
| Substep | Token Category | Examples |
|---------|----------------|----------|
| 3.1.2.1 | Font families | `fontFamilyBase`, `fontFamilyMonospace` |
| 3.1.2.2 | Font sizes | `fontSizeBase200` through `fontSizeBase600` |
| 3.1.2.3 | Font weights | `fontWeightRegular`, `fontWeightSemibold` |
| 3.1.2.4 | Line heights | `lineHeightBase200` through `lineHeightBase600` |

#### Step 3.1.3: Spacing & Layout Tokens
| Substep | Token Category | Examples |
|---------|----------------|----------|
| 3.1.3.1 | Spacing scale | `spacingHorizontalS`, `spacingVerticalM` |
| 3.1.3.2 | Border radius | `borderRadiusSmall`, `borderRadiusMedium` |
| 3.1.3.3 | Shadow elevation | `shadow4`, `shadow8`, `shadow16` |
| 3.1.3.4 | Breakpoints | `breakpointSmall`, `breakpointMedium`, `breakpointLarge` |

### Phase 3.2: Theme Configuration

#### Step 3.2.1: Base Theme Structure
| Substep | Task | Description |
|---------|------|-------------|
| 3.2.1.1 | Extend Fluent light theme | Override brand colors |
| 3.2.1.2 | Create dark theme variant | Inverted color mappings |
| 3.2.1.3 | High contrast support | WCAG AAA compliance |
| 3.2.1.4 | Theme switching mechanism | Context + localStorage persistence |

#### Step 3.2.2: Banking-Specific Extensions
| Substep | Task | Description |
|---------|------|-------------|
| 3.2.2.1 | Money formatting tokens | Currency symbol placement, decimals |
| 3.2.2.2 | Status color mapping | Transaction status → color |
| 3.2.2.3 | Account type theming | Visual differentiation by account type |
| 3.2.2.4 | Security level indicators | Visual hierarchy for security states |

### Deliverables for Session 3
- [ ] Token inventory spreadsheet
- [ ] Theme configuration files (TypeScript)
- [ ] Color contrast verification report
- [ ] Theme switching implementation spec

---

## SESSION 4: Validation & Gap Analysis

### Objective
Validate the plan against requirements, identify gaps.

### Phase 4.1: Requirements Validation

#### Step 4.1.1: API Coverage Validation
| Substep | Task | Validation |
|---------|------|------------|
| 4.1.1.1 | Map each endpoint to UI | All 29 DTOs have UI representation |
| 4.1.1.2 | Verify mutation flows | Deposit, Withdraw, Transfer have forms |
| 4.1.1.3 | Check error handling | ProblemDetails → user-friendly messages |
| 4.1.1.4 | Validate pagination | Transaction list supports server pagination |

#### Step 4.1.2: Security Flow Validation
| Substep | Task | Validation |
|---------|------|------------|
| 4.1.2.1 | Auth flow coverage | Login, Register, Logout complete |
| 4.1.2.2 | PIN flow coverage | Set, Verify, Step-up modal |
| 4.1.2.3 | Session management | Expiry warning, auto-logout |
| 4.1.2.4 | Protected routes | Route guards match API requirements |

### Phase 4.2: UX Standard Validation

#### Step 4.2.1: Accessibility Audit
| Substep | Task | WCAG Criteria |
|---------|------|---------------|
| 4.2.1.1 | Color contrast | 4.5:1 for text, 3:1 for large text |
| 4.2.1.2 | Keyboard navigation | All interactive elements focusable |
| 4.2.1.3 | Screen reader support | Semantic HTML, ARIA labels |
| 4.2.1.4 | Focus management | Logical focus order, visible focus |

#### Step 4.2.2: Mobile-First Validation
| Substep | Task | Criteria |
|---------|------|----------|
| 4.2.2.1 | Responsive layouts | All pages work 320px-1920px |
| 4.2.2.2 | Touch targets | Min 44x44px for interactive elements |
| 4.2.2.3 | Content hierarchy | Critical info visible without scroll |
| 4.2.2.4 | Form usability | Appropriate keyboard types, autocomplete |

### Phase 4.3: Gap Identification

#### Step 4.3.1: Missing Components
| Substep | Task | Output |
|---------|------|--------|
| 4.3.1.1 | Compare plan vs requirements | Gap list |
| 4.3.1.2 | Identify Fluent UI gaps | Custom component needs |
| 4.3.1.3 | Find missing states | Empty, error, loading gaps |
| 4.3.1.4 | Check edge cases | Offline, timeout, partial data |

#### Step 4.3.2: Missing Flows
| Substep | Task | Output |
|---------|------|--------|
| 4.3.2.1 | Onboarding gaps | First-time user experience |
| 4.3.2.2 | Error recovery gaps | Retry, fallback paths |
| 4.3.2.3 | Help/support gaps | Contextual help, tooltips |
| 4.3.2.4 | Confirmation gaps | Undo, cancel, confirm dialogs |

### Deliverables for Session 4
- [ ] Requirements traceability matrix
- [ ] Accessibility audit report
- [ ] Gap analysis document
- [ ] Priority-ranked gap list

---

## SESSION 5: Plan Corrections & Refinement

### Objective
Address identified gaps, refine component specifications.

### Phase 5.1: Critical Gap Resolution

#### Step 5.1.1: Missing Component Specs
| Substep | Task | Output |
|---------|------|--------|
| 5.1.1.1 | Add missing page specs | Updated page inventory |
| 5.1.1.2 | Add missing component specs | Updated component taxonomy |
| 5.1.1.3 | Define empty states | Empty state patterns document |
| 5.1.1.4 | Define error states | Error handling patterns document |

#### Step 5.1.2: Flow Corrections
| Substep | Task | Output |
|---------|------|--------|
| 5.1.2.1 | Refine onboarding flow | Step-by-step user journey |
| 5.1.2.2 | Add error recovery flows | Retry/fallback specifications |
| 5.1.2.3 | Add confirmation dialogs | Dialog component specs |
| 5.1.2.4 | Add help touchpoints | Tooltip/help icon placement |

### Phase 5.2: Design System Refinement

#### Step 5.2.1: Token Corrections
| Substep | Task | Output |
|---------|------|--------|
| 5.2.1.1 | Add missing tokens | Updated token inventory |
| 5.2.1.2 | Fix contrast issues | Adjusted color values |
| 5.2.1.3 | Standardize spacing | Consistent spacing scale |
| 5.2.1.4 | Verify responsive tokens | Breakpoint-aware values |

### Deliverables for Session 5
- [ ] Corrected page inventory
- [ ] Corrected component taxonomy
- [ ] Updated design token system
- [ ] Flow diagrams for corrected flows

---

## SESSION 6: Re-evaluation & Final Verification

### Objective
Verify all corrections, ensure completeness.

### Phase 6.1: Completeness Check

#### Step 6.1.1: Coverage Verification
| Substep | Task | Criteria |
|---------|------|----------|
| 6.1.1.1 | API coverage 100% | All endpoints have UI |
| 6.1.1.2 | Security flows complete | All auth patterns covered |
| 6.1.1.3 | Error states defined | All error scenarios handled |
| 6.1.1.4 | Empty states defined | All empty scenarios handled |

#### Step 6.1.2: Consistency Check
| Substep | Task | Criteria |
|---------|------|----------|
| 6.1.2.1 | Naming consistency | Component names follow pattern |
| 6.1.2.2 | Token usage consistency | Tokens used uniformly |
| 6.1.2.3 | Pattern consistency | Similar flows use same patterns |
| 6.1.2.4 | Accessibility consistency | All components meet WCAG |

### Phase 6.2: Stakeholder Review Prep

#### Step 6.2.1: Documentation Finalization
| Substep | Task | Output |
|---------|------|--------|
| 6.2.1.1 | Create summary document | Executive summary |
| 6.2.1.2 | Create visual mockup references | Wireframe links |
| 6.2.1.3 | Create implementation guide | Developer handoff doc |
| 6.2.1.4 | Create testing checklist | QA acceptance criteria |

### Deliverables for Session 6
- [ ] Final verification report
- [ ] Implementation-ready specification
- [ ] Developer handoff documentation
- [ ] QA testing checklist

---

## SESSION 7: Fixes & Implementation Readiness

### Objective
Final fixes, prepare for implementation.

### Phase 7.1: Final Adjustments

#### Step 7.1.1: Last-Minute Fixes
| Substep | Task | Output |
|---------|------|--------|
| 7.1.1.1 | Address review feedback | Updated specs |
| 7.1.1.2 | Fix documentation gaps | Complete docs |
| 7.1.1.3 | Finalize file structure | Folder structure spec |
| 7.1.1.4 | Create implementation order | Prioritized task list |

### Phase 7.2: Implementation Kickoff Prep

#### Step 7.2.1: Sprint Planning Support
| Substep | Task | Output |
|---------|------|--------|
| 7.2.1.1 | Create user stories | Story templates |
| 7.2.1.2 | Estimate complexity | Story point estimates |
| 7.2.1.3 | Identify dependencies | Dependency graph |
| 7.2.1.4 | Define done criteria | Acceptance criteria per story |

### Deliverables for Session 7
- [ ] Final implementation spec
- [ ] Sprint-ready user stories
- [ ] Complexity estimates
- [ ] Implementation kickoff checklist

---

## Page Inventory Summary

Based on API analysis and banking UX standards:

### Public Routes (Unauthenticated)

| Route | Page | Components | Priority |
|-------|------|------------|----------|
| `/` | Landing | Hero, Features, CTA | P1 |
| `/login` | Login | LoginForm | P1 |
| `/register` | Register | RegisterForm (multi-step) | P1 |

### Protected Routes (Authenticated)

| Route | Page | Components | Priority |
|-------|------|------------|----------|
| `/dashboard` | Dashboard Home | AccountCards, RecentTransactions, QuickActions | P1 |
| `/accounts` | Account List | AccountGrid, CreateAccountButton | P1 |
| `/accounts/:id` | Account Detail | AccountHeader, TransactionList, Actions | P1 |
| `/transactions` | Transaction History | TransactionTable, Filters, Pagination | P1 |
| `/transactions/:id` | Transaction Detail | TransactionInfo, Receipt | P2 |
| `/transfer` | Transfer Hub | TransferTypeSelector | P1 |
| `/transfer/internal` | Internal Transfer | InternalTransferForm | P1 |
| `/transfer/external` | External Transfer | RecipientSearch, ExternalTransferForm | P1 |
| `/deposit` | Deposit | DepositForm | P1 |
| `/withdraw` | Withdraw | WithdrawForm, PINVerification | P1 |
| `/settings` | Settings | SettingsTabs | P2 |
| `/settings/profile` | Profile | ProfileForm | P2 |
| `/settings/security` | Security | PINManagement, SessionList | P2 |
| `/settings/preferences` | Preferences | ThemeSwitcher, Notifications | P3 |

### Modal/Overlay Components

| Component | Trigger | Content |
|-----------|---------|---------|
| PINVerificationModal | Step-up auth required | PIN input + biometric option |
| ConfirmTransferDialog | Transfer submission | Summary + confirm/cancel |
| SessionExpiryWarning | 5 min before timeout | Extend/Logout options |
| CreateAccountDrawer | New account action | Account type + name form |

---

## Component Inventory Summary

### Atoms (30+ components)

| Category | Components |
|----------|------------|
| **Buttons** | PrimaryButton, SecondaryButton, IconButton, LinkButton |
| **Inputs** | TextInput, PasswordInput, AmountInput, SearchInput, PINInput |
| **Display** | Text, Heading, Badge, Avatar, Icon, Skeleton |
| **Feedback** | Spinner, ProgressBar, Toast, Alert |

### Molecules (20+ components)

| Category | Components |
|----------|------------|
| **Forms** | LabeledInput, CurrencyInput, RecipientSearchField |
| **Cards** | AccountCard, TransactionItem, RecipientCard, StatCard |
| **Navigation** | NavItem, Breadcrumb, TabItem |

### Organisms (15+ components)

| Category | Components |
|----------|------------|
| **Layout** | AppShell, Header, Sidebar, Footer |
| **Features** | TransferForm, TransactionList, AccountOverview, PINVerification |
| **Data** | DataGrid, PaginatedList, FilterBar |

### Templates (4 layouts)

| Template | Usage |
|----------|-------|
| AuthLayout | Login, Register |
| DashboardLayout | All authenticated pages |
| FormLayout | Multi-step forms |
| DetailLayout | Entity detail pages |

---

## Success Criteria

### Session Completion Criteria

| Session | Exit Criteria |
|---------|---------------|
| Session 1 | All pages inventoried, route structure defined |
| Session 2 | All components categorized, Fluent UI mapping complete |
| Session 3 | Token system defined, theme config created |
| Session 4 | All gaps identified, prioritized |
| Session 5 | All critical gaps resolved |
| Session 6 | Completeness verified, docs finalized |
| Session 7 | Implementation-ready, sprint backlog created |

### Quality Gates

- [ ] 100% API endpoint coverage
- [ ] WCAG 2.2 AA compliance
- [ ] Mobile-first responsive design
- [ ] Fluent UI v9 alignment
- [ ] TypeScript type safety
- [ ] Component reusability > 70%

---

## References

### Enterprise Banking UI/UX
- [Enterprise UX Design in 2026](https://www.wearetenet.com/blog/enterprise-ux-design)
- [Banking App UI Best Practices](https://procreator.design/blog/banking-app-ui-top-best-practices/)
- [Fintech UX Design Patterns](https://www.eleken.co/blog-posts/modern-fintech-design-guide)
- [Banking Onboarding Best Practices](https://craftinnovations.global/banking-onboarding-best-practices-revolut-nubank-monzo/)

### Accessibility
- [WCAG Guidelines for FinTech](https://ailleron.com/insights/how-to-design-accessible-fin-tech-and-bank-solutions/)
- [Accessible Banking Design](https://vasscompany.com/us-can/en/insights/blogs-articles/accessibility-in-banking/)

### Fluent UI & React
- [Fluent UI React v9](https://react.fluentui.dev/)
- [Fluent UI GitHub](https://github.com/microsoft/fluentui)

### Design Systems
- [Design Tokens in React](https://www.uxpin.com/studio/blog/managing-global-styles-in-react-with-design-tokens/)
- [TypeScript Design Tokens](https://mlm.dev/posts/typescript-design-tokens-with-styled-components)

---

## Appendix A: AzureBank API → UI Mapping

| API Endpoint | Method | UI Component | Page |
|--------------|--------|--------------|------|
| `/auth/login` | POST | LoginForm | `/login` |
| `/auth/register` | POST | RegisterForm | `/register` |
| `/auth/logout` | POST | LogoutButton | Header |
| `/auth/me` | GET | UserContext | App-wide |
| `/auth/pin` | POST | SetPINForm | `/settings/security` |
| `/auth/pin/verify` | POST | PINVerificationModal | Modal |
| `/accounts` | GET | AccountList | `/accounts`, `/dashboard` |
| `/accounts` | POST | CreateAccountForm | Drawer |
| `/accounts/{id}` | GET | AccountDetail | `/accounts/:id` |
| `/accounts/{id}/full-number` | GET | FullNumberReveal | `/accounts/:id` |
| `/transactions` | GET | TransactionList | `/transactions` |
| `/transactions/{id}` | GET | TransactionDetail | `/transactions/:id` |
| `/transactions/deposit` | POST | DepositForm | `/deposit` |
| `/transactions/withdraw` | POST | WithdrawForm | `/withdraw` |
| `/transfers` | POST | ExternalTransferForm | `/transfer/external` |
| `/transfers/internal` | POST | InternalTransferForm | `/transfer/internal` |
| `/users/search` | GET | RecipientSearch | `/transfer/external` |
| `/users/{azureTag}` | GET | RecipientLookup | `/transfer/external` |

---

## Appendix B: Fluent UI v9 Component Mapping

| AzureBank Need | Fluent UI v9 Component | Notes |
|----------------|------------------------|-------|
| Form inputs | `Input`, `Field` | Use Field wrapper for labels |
| Dropdowns | `Dropdown`, `Combobox` | Combobox for search |
| Buttons | `Button` | Use appearance prop |
| Cards | `Card` | Flexible container |
| Data grids | `DataGrid` | From `@fluentui/react-table` |
| Dialogs | `Dialog` | Modal interactions |
| Drawers | `Drawer` | Side panel |
| Toasts | `Toast`, `Toaster` | Notifications |
| Tabs | `TabList`, `Tab` | Section navigation |
| Menus | `Menu`, `MenuItem` | Dropdown menus |
| Progress | `ProgressBar`, `Spinner` | Loading states |
| Skeleton | `Skeleton` | Loading placeholders |
| Avatar | `Avatar` | User/account images |
| Badge | `Badge` | Status indicators |
| Tooltip | `Tooltip` | Contextual help |

---

*Document Version: 1.0*
*Next Review: After Session 1 completion*
