# ADR-0025: The originals are a reference mine, not a code source

**Status**: Accepted

**Date**: 2026-07-25

**Decision Makers**: Vladislav Aleshaev

---

## Context

An earlier version of this application exists, written before the current rebuild. It is a genuine
asset: its flows, its screen designs and its component structure are better developed than anything
written down here, and it has been mined repeatedly for UI/UX direction.

It is also a hazard, because "the original did it this way" is a persuasive premise that is easy to
state and expensive to check. Three separate versions of that premise survived until an adversarial
investigation disproved them — twenty-eight agents across seven analysis passes and twenty-one
verification passes, three knowledge graphs, roughly 1.9 million tokens and 481 file reads. The
highest-severity finding is that **porting the originals' idempotency filter would reintroduce a
real double-spend window** into code that currently does not have one.

None of that is recorded anywhere versioned. The canonical path of the preserved copies appears in
**zero** repository files, and the findings live in a planning document that is being archived. So
the cheapest way to re-learn any of it is to run the investigation again.

## Decision

1. **`C:\Dev2\azurebank-originals\playground-backupcomplete` is the single canonical source** for
   all future mining — backend, frontend and documentation alike. The `active` copy is consulted
   for exactly one thing, its `README-FULL.md`, when polishing the root README;
   `frontend-only-TemporalyBackup` is mined out and holds nothing further. **A "port this from the
   original" task that does not cite backupcomplete is rejected on that basis alone.**

2. **The current repository is always the base.** The originals supply specifications, design
   reference and UI/UX direction. They never supply replacement code.

3. **Do-not-port register.** Each entry was verified adversarially against the actual source, and
   each is a correctness or security **regression** relative to what ships today:
   - Their `TransferService` — a single `SaveChanges` with no database transaction and no retry, so
     a concurrent same-account transfer either 500s or races.
   - Their **idempotency filter** — three non-atomic `SaveChanges` calls, leaving a real
     double-spend window on a retry after `ProcessingTimeout`; the unique index that would have
     saved it is dead and unpopulated.
   - Their **PIN lockout** — claimed in commit messages and **absent from the code**;
     `VerifyPinAsync` has no attempt limiting at all.
   - Their **auth model** — the JWT is written straight into the browser.
   - Their **test posture** — there is no xUnit project.

4. **Corrected false premises.** Each of these was a working assumption that cost real time before
   it was disproved, so each is recorded rather than left to be re-formed:
   - `TransferType.Scheduled` and `TransferType.Reversal` are **dead enum members in both
     originals** — zero usages, never shipped. They will not be built here, and
     `TransactionStatus.Reversed` already covers the display need.
   - The originals' frontend is **plain Redux Toolkit, not Zustand**. No state-library migration may
     be justified by "that is what the original did".
   - The backend is roughly 99% identical between the two preserved copies — the apparent 99-file
     difference was about 96% line-ending noise — so no feature is unique to one copy on that basis.

## Alternatives considered

**Treat the originals as a branch to merge from.** Rejected on the evidence above: in the four
paths that matter most, merging their code backwards is a regression, and the diff noise makes the
comparison unreliable at file granularity.

**Delete the originals now that the design has been mined.** Rejected: the UI/UX work is not fully
extracted, and the design phase still consults them. This ADR fixes their location precisely so
they can be kept without being trusted.

**Record only the location and leave the findings in the archive.** Rejected: the location without
the register is what produces the "the original had X, should we bring it back" cycle, which is the
recurring cost this ADR exists to end.

## Residuals (accepted, documented)

- **The canonical copies live outside the repository**, on one machine, in `C:\Dev2`. They are not
  backed up by anything git-shaped. If that disk fails, the design reference is gone — accepted for
  now, because the material that mattered most has been extracted into planning documents and this
  ADR.
- **The do-not-port register is a snapshot.** It was verified against the originals as they stood;
  since the originals are frozen, it will not drift — but it also will not grow. A future mining
  pass that finds a fifth hazard should add it here.
- **"Reference mine" is a judgement call per item.** This ADR says where to look and what never to
  copy; it cannot enumerate in advance everything that is safe to adapt. The rule of thumb is that
  anything touching transfers, idempotency, auth or lockout is a spec-level adaptation into the
  existing atomic and fenced machinery, never a file copy.

## Consequences

**Positive** — the location of the reference is fixed and versioned; four known regressions cannot
be reintroduced by an appeal to the original; three false premises stop being re-derived; the
recurring "should we bring back X" cycle has a document to terminate it.

**Negative** — a genuinely good idea in one of the four banned paths now needs to be re-derived as
a specification rather than lifted, which is slower. That cost is accepted deliberately: those four
paths are exactly where a fast copy is most dangerous.

## Related

- **ADR-0009** — the atomic idempotency machinery their filter must never replace.
- **ADR-0010** — the PIN lockout theirs claims but does not implement.
- **ADR-0019** — the SPA/BFF auth model that supersedes their browser-held JWT.
- **ADR-0021** — refresh-token rotation, the one item from the originals that was genuinely adopted.
- **ADR-0024** — the originals-audit item that was researched and rejected.
