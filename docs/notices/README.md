# The rendered notices

`pin-enrolled.sample.eml` and `pin-changed.sample.eml` are what a runner writes for a notice the API
recorded as owed to an account holder: one RFC 5322 message per notice, rendered from the row and
addressed to the email held on the account. There is one per kind: an enrolment (ADR-0045) and a
change of an existing PIN (ADR-0047). The words differ on purpose: a change costs the current PIN
and never the password, so the enrolment's "for the first time" and "your account password was
proved" would both be false in it.

These are **samples**, each produced by the `notify` verb against a throwaway database. The
addresses and the contact line are placeholders, and the reference in each is the notice id of that
run and names nothing that exists anywhere else. Both files are committed byte for byte, CRLF
included (`.gitattributes`).

## What this does NOT show

**Delivery.** A file in a pickup directory has reached the edge of the machine that wrote it and
nobody else. Nothing in this repository sends:
[`relaying-the-enrolment-notice.md`](../deferred/relaying-the-enrolment-notice.md) records what
would, and why it is not built. Read each file as "what the account holder would open", not as "what
the account holder received".

## What a reader can check with no key

- The `Message-ID` is the notice's id: the file was rendered from the `SubscriberNotices` row with
  that `Id`, and `DeliveryReceipt` on that row is the file's name. The statement that finds the row
  from a reference is in [`pin-enrolment-repudiated.md`](../runbooks/pin-enrolment-repudiated.md).
- Nothing in the body could be used against the reader: no PIN, no password, no account number, no
  name, no link, and no advice that signing out ends the compromise.
- The sender domain is `.invalid` (RFC 2606): it resolves nowhere and impersonates nobody.

## How to produce a fresh one

Enrol a PIN through the API, running against a database at the latest migration
([local setup](../engineering-practices.md#local-setup)). Then run the verb from the repository
root, in a terminal whose environment holds the same connection string and audit keys as the API:
`ConnectionStrings__DefaultConnection`, `Audit__ChainKey` and `Audit__AnchorKey`.

```bash
dotnet run --project backend/tools/AzureBank.AuditVerifier -- notify ../azurebank-notices \
  --contact "security@your-bank.example, +00 000 0000"
```

- **The directory must exist and be outside any git repository.** The verb refuses one inside a
  working tree, and does not create one.
- **Delete the directory afterwards**: it holds addresses in clear.
- **`--contact` is required**: it is how a recipient says "this was not me", and no notice is
  rendered without it.

## The hosted runners

The same file is written, through the same transport, by a loop in the API (ADR-0048) and by an
Azure Function (ADR-0051). The Function's
[README](../../backend/src/AzureBank.Functions.NoticeRelay/README.md) says how to run it and lists
the `Notices` settings. The API's relay needs `Notices__Runner=Api`, `Notices__PickupDirectory` and
`Notices__Contact`, and a partial set stops the API at start (ADR-0048 D6).

- **`Notices:Runner` names one hosted runner: `Api`, `Function` or `None`**, the default. Each host
  steps aside unless the flag names it, so a configuration that names neither delivers nothing and
  an owed notice waits. A host that steps aside says so in its log, at Information:
  `Notice relay: runner is None; this process delivers nothing (Notices:Runner)`.
- **The flag does not gate the verb.** `notify` delivers whenever a person runs it,
  `Notices:Runner=None` included. It takes the same lease a host takes (ADR-0048 D5): it claims what
  it delivers under its own name, and takes a row another runner holds only once that runner's lease
  has lapsed. So the verb is safe beside a running relay, and beside one that has died.
- **Whichever runner filled the pickup directory, delete it afterwards.**
