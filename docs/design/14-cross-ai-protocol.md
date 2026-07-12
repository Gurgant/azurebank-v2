# Cross-AI Communication Protocol
## Claude <-> Gemini Coordination

**Document Version**: 1.0
**Created**: 2025-12-16
**Status**: ACTIVE

---

## 1. Purpose

This document defines the protocol for coordinating between Claude (primary AI team) and Gemini (external review team) for design validation and quality assurance.

---

## 2. Communication Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                     COMMUNICATION FLOW                           │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  ┌──────────────┐                        ┌──────────────┐       │
│  │              │   1. Prepare Prompt    │              │       │
│  │    CLAUDE    │ ─────────────────────► │    HUMAN     │       │
│  │    (Team)    │                        │  (Mediator)  │       │
│  │              │                        │              │       │
│  └──────────────┘                        └──────────────┘       │
│         ▲                                       │               │
│         │                                       │               │
│         │  4. Process Response                  │ 2. Execute    │
│         │                                       ▼               │
│  ┌──────────────┐                        ┌──────────────┐       │
│  │              │   3. Return Response   │              │       │
│  │    CLAUDE    │ ◄───────────────────── │   GEMINI     │       │
│  │    (Team)    │                        │   (Review)   │       │
│  │              │                        │              │       │
│  └──────────────┘                        └──────────────┘       │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 3. Handoff Types

### 3.1 Frontend Design Review (Phase 2)
- **Trigger**: After Claude team produces Frontend Design Report v1
- **Input**: Technical requirements + Design report
- **Expected Output**: External review with recommendations
- **Response Document**: 04h-gemini-review-response.md

### 3.2 Ad-Hoc Reviews (As Needed)
- **Trigger**: Explicit request from human or Claude team
- **Input**: Specific question or design decision
- **Expected Output**: Independent opinion with rationale

---

## 4. Prompt Structure for Gemini

### 4.1 Standard Template
```markdown
# Gemini External Review Request

## Context
[Brief context about what is being reviewed]

## Your Team
- External UX/UI Expert
- External Web Designer
- External Frontend Architect
- Industry Researcher (web search enabled)

## Input Materials
[Attach relevant documents]

## Your Tasks
[Numbered list of specific tasks]

## Output Required
[Specify expected output format]

## Constraints
[List any non-negotiable constraints]
```

### 4.2 Response Requirements
Gemini should provide:
1. Executive Summary
2. Validation Results (what's good)
3. Identified Gaps or Issues
4. Prioritized Recommendations
5. Alternative Approaches (if applicable)
6. Risk Assessment

---

## 5. Human Mediator Instructions

When a Gemini handoff is requested:

1. **Locate the prompt**: Find the prepared prompt in the specified document
2. **Open Gemini**: Use Google AI Studio or Gemini interface
3. **Paste prompt**: Copy the complete prompt to Gemini
4. **Attach materials**: If referenced, include relevant documents
5. **Execute**: Submit and wait for response
6. **Capture response**: Copy Gemini's complete response
7. **Store response**: Paste into the specified response document
8. **Signal Claude**: Inform Claude that response is ready

---

## 6. Confrontation Protocol

After receiving Gemini's response:

### 6.1 Agreement Handling
If Claude team agrees with Gemini's recommendation:
- Document agreement in 04i-cross-ai-confrontation.md
- Incorporate recommendation into final design
- Credit both teams in documentation

### 6.2 Disagreement Handling
If Claude team disagrees with Gemini's recommendation:
1. State Claude team's original position
2. State Gemini's contrary position
3. Analyze trade-offs of each approach
4. Present both options to human for decision
5. Document resolution and rationale

### 6.3 Resolution Format
```markdown
### Confrontation: [Topic]

**Claude Position**: [Summary]
**Gemini Position**: [Summary]

**Trade-offs**:
| Aspect | Claude Approach | Gemini Approach |
|--------|-----------------|-----------------|
| [Factor 1] | [Pro/Con] | [Pro/Con] |
| [Factor 2] | [Pro/Con] | [Pro/Con] |

**Resolution**: [Final decision]
**Rationale**: [Why this decision]
**Decided by**: [Human/Consensus]
```

---

## 7. Timing Guidelines

| Phase | Gemini Review Point | Expected Duration |
|-------|---------------------|-------------------|
| Phase 2 | After Design Report v1 | Human-dependent |
| Phase 4 | After API Contract (optional) | Human-dependent |
| Phase 9 | Final Review (optional) | Human-dependent |

---

## 8. Best Practices

### For Claude Team
- Prepare complete, self-contained prompts
- Include all relevant context
- Specify clear deliverables
- Be open to different perspectives
- Document all confrontations fairly

### For Human Mediator
- Execute prompts as written
- Capture complete responses
- Don't filter or modify responses
- Communicate clearly when responses are ready

### For Gemini (instructions in prompt)
- Use web search for industry research
- Provide specific, actionable feedback
- Prioritize recommendations
- Be constructive and solution-oriented

---

## 9. Error Handling

### 9.1 Gemini Unavailable
If Gemini cannot be accessed:
- Claude team proceeds with internal design
- Document that external review was skipped
- Add risk note about lack of external validation

### 9.2 Unclear Response
If Gemini's response is unclear:
- Human may request clarification
- Or Claude team interprets best effort
- Document any assumptions made

### 9.3 Conflicting Requirements
If Gemini suggests violating hard constraints:
- Claude team rejects that specific recommendation
- Document the rejection and reason
- Apply remaining valid recommendations

---

## 10. Document Trail

All cross-AI communications are documented:

| Document | Purpose |
|----------|---------|
| 04g-gemini-review-prompt.md | Prompt sent to Gemini |
| 04h-gemini-review-response.md | Gemini's response |
| 04i-cross-ai-confrontation.md | Confrontation resolution |
| 04j-frontend-design-final.md | Merged final design |
| 13-review-notes.md | Overall notes and outcomes |

---

**Status**: Protocol ACTIVE - Ready for Phase 2 execution
