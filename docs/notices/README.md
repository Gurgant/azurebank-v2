# The rendered notices

`pin-enrolled.sample.eml` and `pin-changed.sample.eml` are what
`AzureBank.AuditVerifier notify <directory> --contact "<text>"`
writes — and what the API's own relay writes when `Notices:Runner=Api` (ADR-0048), through the same
transport: one RFC 5322 message per notice the API recorded as owed to an account holder, rendered
from the row and addressed to the email held on the account. One per kind: an enrolment
(ADR-0045) and a change of an existing PIN (ADR-0047). They are deliberately not the same words —
a change costs the current PIN and never the password, so the enrolment's "for the first time" and
"your account password was proved" would both be false in it.

These are **samples**. Each was produced by the real verb against a throwaway database, so that a
reader can see the shape without running anything. The addresses and the contact line are
placeholders; the reference in each is a real notice id from that run and names nothing that exists
anywhere else.

## What this does NOT show

**Delivery.** A file in a pickup directory has reached the edge of the machine that wrote it and
nobody else. Nothing in this repository sends; `docs/deferred/relaying-the-enrolment-notice.md`
records what would, and why it is not here. Read the file as "what the account holder would open",
not as "what the account holder received".

## What a reader can check with no key

- The `Message-ID` is the notice's id. `SELECT * FROM SubscriberNotices WHERE Id = '<that id>'` is
  the row it was rendered from, and `DeliveryReceipt` on that row is this file's name.
- Nothing in the body could be used against the reader: no PIN, no password, no account number, no
  name, no link, and no instruction that signing out ends the compromise.
- The sender domain is `.invalid` (RFC 2606): it resolves nowhere and impersonates nobody.

## How to produce a fresh one

From the repository root, with the six secrets in the environment and a database at the latest
migration: enrol a PIN through the API, then

    dotnet run --project backend/tools/AzureBank.AuditVerifier -- notify ../azurebank-notices \
      --contact "security@your-bank.example, +00 000 0000"

The directory must exist and must be outside any git repository; the verb refuses one inside a
working tree. Delete the directory afterwards — it holds addresses in clear.
