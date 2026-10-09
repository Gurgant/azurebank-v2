# Deferred: what was chosen not to build, and why

Each page in this folder is work that was understood well enough to build and was not built. It says
what the thing is, what it would buy, the specific reason it is out of scope **for this
deployment**, and what would have to be true for it to be worth doing.

A gap that nobody wrote down looks the same as a gap that nobody noticed, and "not now" is a
decision with a trigger: these pages record both.

## What belongs here, and what does not

A rejected idea belongs in a decision record ([`docs/adr/`](../adr/README.md)), with the reasoning
that rejected it. Something that would be built if the deployment were different belongs here. The
difference is whether a change in circumstances would change the answer. Nothing here is a to-do
list.

## Contents

- [`anchoring-the-audit-trail.md`](anchoring-the-audit-trail.md): the audit chain cannot detect rows
  deleted from its end. Closing that needs time issued by a third party, on a schedule, and a copy
  published where the deployment's administrator cannot revise it. SQL Server's ledger closes a
  different layer, the write, and is deferred too (ADR-0044 D2).
- [`relaying-the-enrolment-notice.md`](relaying-the-enrolment-notice.md): the notice an account
  holder is owed when a PIN is enrolled or changed is recorded with the event and rendered into a
  pickup directory, and nothing sends it. Sending needs a provider and its credential, addresses
  proved by their holders, a second address and a PIN reset behind the contact (ADR-0045, ADR-0048).
