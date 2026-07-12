# Claude Post-Review Prompt
## Process Gemini's External Review Feedback

**Document Version**: 1.0
**Created**: 2025-12-17
**Purpose**: Prompt for Claude to use after Gemini completes their review
**Status**: READY FOR USE

---

## When to Use This Document

Use this prompt with Claude Code **AFTER** Gemini has completed their review and created files in `project-docs-gemini/frontend-design-gemini/`.

---

## How to Use

Copy the content between `=== BEGIN PROMPT ===` and `=== END PROMPT ===` and paste it into Claude Code.

---

=== BEGIN PROMPT ===

# Post-Review Task: Process Gemini's External Feedback

## Context

Gemini has completed their external review of the AzureBank frontend design. Their feedback is in:
- `project-docs-gemini/frontend-design-gemini/review-report.md`
- `project-docs-gemini/frontend-design-gemini/recommendations.md`
- (Possibly other files they created)

## Your Tasks

### Task 1: Read Gemini's Feedback

1. Read all files in `project-docs-gemini/frontend-design-gemini/`
2. Identify:
   - What they praised (validate our decisions)
   - Critical issues they found
   - Recommendations they made
   - Alternative approaches suggested
   - Questions they have

### Task 2: Conduct Confrontation

For each issue or recommendation Gemini raised, analyze and respond:

**Template for each confrontation:**
```markdown
### Confrontation: [Topic]

**Gemini Position**: [What Gemini said]

**Claude Team Response**:
- [ ] ACCEPT - We agree, will implement
- [ ] REJECT - We disagree, here's why: [reason]
- [ ] COMPROMISE - Partial acceptance: [details]

**Trade-off Analysis**: [Pros and cons of each position]

**Resolution**: [Final decision]

**Action Items**: [What changes to make, if any]
```

### Task 3: Update Cross-AI Confrontation Document

Update `project-docs/frontend-design/04i-cross-ai-confrontation.md` with:
- All confrontations analyzed
- Resolutions reached
- Action items identified

### Task 4: Update Documents if Needed

If we accept any of Gemini's recommendations, update the relevant documents:
- `04a-ux-user-flows.md`
- `04b-ux-wireframes.md`
- `04c-design-visual-specs.md`
- `04d-design-tokens.md`
- `04e-frontend-components.md`
- `04j-frontend-design-final.md`

### Task 5: Create Summary

Provide a summary of:
- Issues accepted and changes made
- Issues rejected with reasons
- Compromises reached
- Overall verdict on Gemini's review quality

---

## Decision Framework

When deciding whether to accept Gemini's feedback:

**ACCEPT if**:
- They identified a genuine gap we missed
- Their suggestion improves user experience
- Their suggestion improves maintainability
- It's within MVP scope and reasonable effort

**REJECT if**:
- They misunderstood our constraints
- Their suggestion conflicts with mandatory tech stack
- The effort outweighs the benefit for MVP
- We already considered and intentionally decided against it

**COMPROMISE if**:
- The core concern is valid but solution is different
- We can address it partially for MVP, fully later
- There's a middle ground that satisfies both

---

## Output Expected

1. Updated `04i-cross-ai-confrontation.md` with all confrontations
2. Any updated design documents (if changes accepted)
3. Summary report of the review process

---

Begin by reading: `project-docs-gemini/frontend-design-gemini/review-report.md`

=== END PROMPT ===

---

## Post-Process Checklist

After using the above prompt:

- [ ] All confrontations documented
- [ ] Accepted changes implemented
- [ ] Rejected items have clear reasoning
- [ ] Final design document updated if needed
- [ ] Ready for implementation phase

---

**Document Status**: READY FOR USE - Use after Gemini review is complete
