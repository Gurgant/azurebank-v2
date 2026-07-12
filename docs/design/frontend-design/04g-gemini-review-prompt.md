# Gemini External Review Prompt
## Bank Account Management System - Frontend Design Review

**Document Version**: 4.0
**Created**: 2025-12-16
**Updated**: 2025-12-17
**Purpose**: External validation by Gemini AI team
**Status**: COMPLETE - Ready for Gemini Review (Enhanced with Industry Standards)

---

## How to Use This Document

Copy the entire content between the `=== BEGIN PROMPT ===` and `=== END PROMPT ===` markers and paste it into Google Gemini (with project file access enabled).

---

=== BEGIN PROMPT ===

# External Review Request: AzureBank Frontend Design

## Important: You Have File Access!

**You can see all files in this project directly.** You do NOT need me to paste content - you can read the files yourself.

---

## Your Role

You are the **External Review Team** for the AzureBank project, consisting of:
- **Senior UX/UI Expert** - User experience and usability
- **Senior Web Designer** - Visual design and design systems
- **Senior Frontend Architect** - Technical architecture and code quality
- **Industry Researcher** - Banking app best practices

---

## Project Structure Overview

```
BankApp/  (AzureBank - Bank Account Management System)
├── project-docs/                    ← CLAUDE'S WORK (Read-only for you)
│   ├── frontend-design/             ← Frontend design documents
│   │   ├── 04a-ux-user-flows.md     ← User flows and validation rules
│   │   ├── 04b-ux-wireframes.md     ← Desktop and mobile wireframes
│   │   ├── 04c-design-visual-specs.md ← Component visual specifications (v4.0)
│   │   ├── 04d-design-tokens.md     ← FluentUI theme configuration
│   │   ├── 04e-frontend-components.md ← React component architecture (v4.0)
│   │   ├── 04h-internal-confrontation.md ← Claude's internal review
│   │   ├── 04j-frontend-design-final.md ← CONSOLIDATED FINAL (Start Here!)
│   │   ├── 04l-external-transfers-design.md ← External transfers with AzureTag
│   │   └── 04m-industry-standards-analysis.md ← Industry research & gap analysis (NEW!)
│   ├── 09-api-contracts.md          ← API endpoint specifications
│   ├── 10-tech-stack-choices.md     ← Technology decisions
│   └── 16-package-manifest.md       ← Package versions and choices
│
├── project-docs-gemini/             ← YOUR WORKSPACE (Create files here!)
│   ├── README.md                    ← Instructions for you
│   └── frontend-design-gemini/      ← Put your review files here
│       └── README.md                ← Your task checklist
│
└── src/                             ← (Not yet created - design phase)
```

---

## Your Tasks

### Step 1: Read Claude's Work

Start by reading these files in order:

1. **`project-docs/frontend-design/04j-frontend-design-final.md`** - Consolidated design (START HERE)
2. **`project-docs/frontend-design/04l-external-transfers-design.md`** - External transfers with AzureTag (@username) system
3. **`project-docs/frontend-design/04m-industry-standards-analysis.md`** - Industry research findings (NEW!)
4. **`project-docs/frontend-design/04a-ux-user-flows.md`** - All user flows
5. **`project-docs/frontend-design/04e-frontend-components.md`** - Technical architecture with enhanced components
6. **`project-docs/frontend-design/04h-internal-confrontation.md`** - What they found internally

### Step 2: Perform Your Review

Evaluate against these criteria:

**UX/UI (40%)**
- Are user flows complete and logical?
- Are error states handled properly?
- Is the mobile experience well thought out?
- Does it meet WCAG 2.1 AA accessibility?

**Visual Design (20%)**
- Is the design system consistent?
- Are colors appropriate for banking?
- Is typography readable and hierarchical?
- Is the responsive strategy sound?

**Technical Architecture (30%)**
- Is the component structure scalable?
- Is RTK Query used correctly?
- Are TypeScript interfaces well-designed?
- Are there performance concerns?
- Are the new enhanced components (Skeleton, LoadingButton, AnimatedNumber, ProgressStepper, SuccessAnimation) well-designed?

**Industry Standards (10%)**
- Does it follow banking app conventions?
- Are there missing expected features?
- Security considerations?
- Does the AzureTag (@username) system align with industry practices?
- Is the 4-step external transfer wizard appropriate?

### Step 3: Create Your Deliverables

**Create files in `project-docs-gemini/frontend-design-gemini/`:**

#### Required: `review-report.md`
```markdown
# External Review Report
## Bank Account Management System - Frontend Design

### Executive Summary
[2-3 paragraphs overall assessment]

### 1. Strengths Identified
[What Claude's team did well - be specific]

### 2. Critical Issues (Must Fix Before Implementation)
[Issues that could cause project failure]

### 3. Recommendations (Should Fix)
[Significant improvements needed]

### 4. Suggestions (Nice to Have)
[Minor improvements or future considerations]

### 5. Questions for Claude Team
[Clarifications needed]

### 6. Final Verdict
[ ] Ready for implementation
[ ] Needs minor revisions
[ ] Needs significant revisions
[ ] Needs complete rework

### 7. Detailed Findings by Category

#### UX/UI Findings
[Detailed analysis]

#### Visual Design Findings
[Detailed analysis]

#### Technical Architecture Findings
[Detailed analysis]

#### Industry Standards Findings
[Detailed analysis]
```

#### Required: `recommendations.md`
```markdown
# Detailed Recommendations

## Priority 1: Critical (Must Fix)
| Issue | Location | Recommendation |
|-------|----------|----------------|

## Priority 2: Important (Should Fix)
| Issue | Location | Recommendation |
|-------|----------|----------------|

## Priority 3: Nice to Have
| Issue | Location | Recommendation |
|-------|----------|----------------|
```

#### Optional: `alternative-approaches.md`
If you disagree with specific decisions, document alternative approaches.

#### Optional: `risk-assessment.md`
Document any project risks you identify.

---

## Rules of Engagement

### You CAN:
- Read ANY file in `project-docs/`
- Create ANY file in `project-docs-gemini/`
- Create subfolders in your workspace
- Be critical and honest
- Propose alternative solutions

### You CANNOT:
- Modify files in `project-docs/` (Claude's territory)
- Delete Claude's documents
- Change the mandatory technology stack

### Mandatory Constraints (Cannot Change):
- FluentUI v9 is the UI framework
- React 19 + TypeScript 5.7 (strict mode)
- Redux Toolkit + RTK Query
- .NET 10 backend (separate team)
- Mobile + Desktop responsive required
- WCAG 2.1 AA accessibility minimum

---

## Project Context

**Project**: Bank Account Management System
**Purpose**: Technical assessment for Dev4Side recruitment
**Type**: Full-stack web application (React frontend, .NET backend)

**Features**:
- User registration and login (BFF session-based auth)
- Create bank accounts (Savings, Checking, Investment)
- View account balances with animated balance display
- Deposit, Withdraw, Transfer (between own accounts)
- **External Transfers** via AzureTag (@username) system (NEW!)
- Transaction history with filtering and date grouping
- Skeleton loading states for all major components
- Success animations for completed transactions

**Industry Standards Applied** (v4.0 Enhancements):
- Skeleton loading (reduces perceived load time by 40%)
- Button press feedback with loading states
- Animated balance updates with countUp effect
- 4-step progress stepper for external transfers
- Success celebration animations
- Transaction date grouping (Today, Yesterday, This Week, etc.)

**Current Status**: Design phase complete with industry standards alignment, awaiting external review before implementation.

---

## Review Guidelines

1. **Be Critical** - The team wants honest feedback, not validation
2. **Be Specific** - Point to exact files/sections, not vague concerns
3. **Be Constructive** - Suggest solutions, not just problems
4. **Consider Context** - This is an MVP for a technical test, not a production app
5. **Respect Constraints** - Don't suggest changing FluentUI, React, Redux, etc.

---

## When You're Done

1. Ensure `review-report.md` and `recommendations.md` exist in your workspace
2. The Claude team will read your files
3. A confrontation meeting will resolve any disagreements
4. Final merged recommendations will be incorporated

---

## Start Now!

Begin by reading: **`project-docs/frontend-design/04j-frontend-design-final.md`**

Then create your review files in: **`project-docs-gemini/frontend-design-gemini/`**

Good luck with your review!

=== END PROMPT ===

---

## Post-Review Process

After Gemini completes the review:

1. Read Gemini's files in `project-docs-gemini/frontend-design-gemini/`
2. Use the Claude post-review prompt (see `04k-claude-post-review-prompt.md`)
3. Document confrontations in `04i-cross-ai-confrontation.md`
4. Update final design document if needed

---

**Document Status**: COMPLETE (v4.0) - Ready to send to Gemini - Enhanced with Industry Standards Analysis
