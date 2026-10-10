# ADR-0025: The originals are a reference mine, not a code source

**Status:** Accepted · **Date:** 2026-07-25 · **Decision Makers:** Vladislav Aleshaev

## Context

An earlier version of this application exists, written before the current rebuild. It is an asset:
its flows, its screen designs and its component structure are better developed than anything written
down here, and it is mined for UI/UX direction. It is also a hazard, because "the original did it
this way" is a premise that is easy to state and expensive to check. Three versions of that premise
survived until they were checked against the actual source and disproved, and the most severe
finding is that porting the originals' idempotency filter would reintroduce a real double-spend
window into code that does not have one.

## Decision

1. **The original project's copy `playground-backupcomplete` is the single canonical source** for
   all mining: backend, frontend and documentation alike. The `active` copy is consulted for one
   thing, its `README-FULL.md`, when the root README is polished, and
   `frontend-only-TemporalyBackup` is mined out. A "port this from the original" task that does not
   cite backupcomplete is rejected on that basis alone.
2. **The current repository is always the base.** The originals supply specifications, design
   reference and UI/UX direction. They never supply replacement code.
3. **Do-not-port register.** Each entry was verified against the actual source, and each is a
   correctness or security regression relative to what ships:
   - Their `TransferService`: a single `SaveChanges` with no database transaction and no retry, so a
     concurrent same-account transfer either answers 500 or races.
   - Their idempotency filter: three non-atomic `SaveChanges` calls, which leave a double-spend
     window on a retry after `ProcessingTimeout`; the unique index that would have saved it is dead
     and unpopulated.
   - Their PIN lockout: claimed in commit messages and absent from the code. `VerifyPinAsync` has no
     attempt limiting at all.
   - Their auth model: the JWT is written straight into the browser.
   - Their test posture: there is no xUnit project.
4. **Corrected false premises**, recorded so that they are not formed again:
   - `TransferType.Scheduled` and `TransferType.Reversal` are dead enum members in both originals:
     no usages, never shipped. They are not built here, and `TransactionStatus.Reversed` already
     covers the display need.
   - The originals' frontend is plain Redux Toolkit, not Zustand. No state-library migration is
     justified by "that is what the original did".
   - The backend is roughly 99% identical between the two preserved copies: the apparent 99-file
     difference is about 96% line-ending noise, so no feature is unique to one copy on that basis.

## Rejected

- Rejected: treating the originals as a branch to merge from, because in the four paths that matter
  most their code is a regression, and the diff noise makes a file-level comparison unreliable.
- Rejected: deleting the originals now that the design has been mined, because the UI/UX work is not
  fully extracted and the design phase still consults them. Fixing their location is what lets them
  be kept without being trusted.
- Rejected: recording only the location, because the location without the register is what produces
  the "the original had X, should it come back" cycle.

## Consequences

- The reference has one fixed location, four known regressions cannot be reintroduced by an appeal
  to the original, and three false premises stop being derived again.
- The rule of thumb: anything that touches transfers, idempotency, auth or lockout is a spec-level
  adaptation into the existing atomic and fenced machinery, never a file copy. A good idea in one of
  those four paths is re-derived as a specification, which is slower. That cost is accepted: those
  are the paths where a fast copy is most dangerous.
- One item from the originals is adopted, refresh-token rotation (ADR-0021), and one is researched
  and rejected, ETag/If-Match (ADR-0024).
- Not covered: everything that is safe to adapt. The record says where to look and what never to
  copy; the rest is a judgement per item.
- Not covered: the canonical copies live outside the repository, on one machine, with no backup in
  git. If that disk fails the design reference is gone. Accepted, because the findings that matter
  most are in this record.
- Not covered: the register is a snapshot. The originals are frozen, so it does not drift, and it
  does not grow by itself: a later mining pass that finds another hazard adds it here.

## Verified by

- Nothing in the tree can hold this record: it is a rule about where code may come from.

## Related

ADR-0009, ADR-0010, ADR-0019, ADR-0021, ADR-0024.
