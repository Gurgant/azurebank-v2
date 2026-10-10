# ADR-NNNN: Title

**Status:** Proposed · **Date:** YYYY-MM-DD · **Decision Makers:** Name Surname

> Copy this file to `NNNN-short-title.md` with the next free number, replace each line of guidance
> and delete this note. The statuses: [ADR Lifecycle](README.md#adr-lifecycle).
>
> - **Size.** One page: about 60 lines, 80 at most, in lines of at most 100 columns. A record whose
>   items many places cite by number may reach 120, so that each cited item keeps a line.
> - **Wording.** Present tense, the system as subject.
> - **Corrections.** A record says what is true now. When the decision changes, the text is
>   rewritten and the Status line gains `**Amended:** YYYY-MM-DD` with the decision or the ADR that
>   amended it. No dated note and no struck text: the earlier wording is in the git history.
> - **Numbers.** The file name, the number and the labels of the decisions never change: a
>   withdrawn item stays under its label as "withdrawn, see ADR-NNNN".

## Context

At most 8 lines: the problem, and the constraint that forces a choice.

## Decision

1. **What the system does, in one sentence**, because of the reason for it.
2. **One item for each decision.** A table stays only as the contract itself, in at most 8 rows.

## Rejected

- Rejected: an alternative, because of the reason. One line for each alternative.

## Consequences

- What is now true, and what it costs.
- Not covered: a limit that is accepted. One line for each limit.

## Revisit when

- Optional, at most 4 lines: an observable condition that would reopen the decision.

## Verified by

- `TestClassName` or `the command`: what it holds. At most 3 lines, of names that exist in the tree.

## Related

ADR-NNNN, ADR-NNNN: numbers only, on one line.
