# Deferred — what I decided not to build, and why

Everything in this folder is work I understood well enough to build and chose not to. Each document
says what the thing is, what it would buy, the specific reason it is out of scope **for this
deployment**, and what would have to be true for it to become worth doing.

I keep these for two reasons.

The first is that a gap nobody wrote down looks identical to a gap nobody noticed. If a control is
missing from a banking codebase, the interesting question is whether the author knew. These
documents answer it.

The second is that "not now" is a decision with a trigger, and a trigger nobody recorded is an
argument that gets had again. Several of these were re-derived more than once before I started
writing them down.

## What belongs here, and what does not

A rejected idea belongs in an ADR, with the reasoning that killed it. Something I would build if the
deployment were different belongs here. The difference is whether a change in circumstances would
change the answer.

Nothing here is a to-do list. If an item is scheduled, it stops being deferred and moves into the
plan.

## Contents

- [`anchoring-the-audit-trail.md`](anchoring-the-audit-trail.md) — the audit chain cannot detect
  rows deleted from its end. Closing that needs time issued by somebody else, which needs something
  running unattended and somewhere the operator cannot revise. Neither exists on a single-machine
  deployment. SQL Server's ledger closes a different layer — the write — and is deferred too;
  ADR-0044 carries both and says why neither replaces the other.
- [`relaying-the-enrolment-notice.md`](relaying-the-enrolment-notice.md) — the notice an account
  holder is owed when a PIN is enrolled is recorded with the enrolment and rendered into a pickup
  directory — by the operator tool, or since ADR-0048 by the API's own relay — and nothing sends
  it. Sending needs a relay: unattended,
  third-party, and outside a demo paid for — and the account holds one unvalidated address, with no
  second one and no PIN reset behind the contact. ADR-0045 says where the built half stops.
