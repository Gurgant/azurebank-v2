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

## Or from the Function, which is the third runner (ADR-0051)

The same file, written by `AzureBank.Functions.NoticeRelay` instead of by the verb or by the API's
loop. It needs the Azure Functions Core Tools and Azurite — the Functions HOST wants a storage
account for its timer's schedule state and its own singleton lease, which is the whole of what
Azurite is doing here; no queue carries a notice, because the row is the obligation (ADR-0048).

Once, to install:

    winget install --id Microsoft.Azure.FunctionsCoreTools
    npm install -g azurite

Then, from the repository root:

    azurite --silent --location $env:TEMP\azurite-azurebank

    cd backend\src\AzureBank.Functions.NoticeRelay
    copy local.settings.sample.json local.settings.json
    (edit ConnectionStrings:DefaultConnection, Notices:PickupDirectory and Notices:Schedule in it)
    func start

⚠️ **`Notices:Schedule` is the one key whose absence does not stop the host.** It is a trigger
expression the Functions runtime binds before any of this project's code runs, so a missing value is
an indexing failure rather than a startup refusal — the host prints these three lines and then keeps
running with nothing scheduled:

    '%Notices:Schedule%' does not resolve to a value.
    Function 'Functions.DeliverOwedNotices' failed indexing and will be disabled.
    Job host started

Measured. Nothing is delivered twice and no notice is lost — the rows simply stay owed — but the
process is up and its exit code is zero, so a reader who does not check the log sees a healthy host
delivering nothing. Every other key in the section stops the host with a message naming it.

`local.settings.json` is gitignored: it is this host's connection string and pickup directory, the
Function's equivalent of the API's user-secrets. The pickup directory must EXIST and must be outside
any git repository — the Function refuses to start otherwise, with a message naming the key, which
is the same rule the verb and the API apply and now the same code.

**Run one HOSTED runner at a time.** `Notices:Runner` names which: `Api`, `Function`, or `None`.
Both hosts step aside unless the flag names them, so a configuration naming neither delivers nothing
— an owed notice waits, and that is the failure worth having.

⚠️ **The flag does not gate the verb.** `notify` above is run by a person and delivers whenever they
run it, `Notices:Runner=None` included. It does not need the flag, because it takes the same lease a
host takes (ADR-0048 D5): it claims what it delivers under its own name, counts and names rows a live
runner holds, and takes them only once that lease has lapsed. So the verb beside a live relay is safe
by the lease, not by configuration.

The API prints which it is at Information on every start:

    Notice relay: runner is Function; this process delivers nothing (Notices:Runner)

and the Function prints one line per sweep, naming itself:

    Notice relay: sweep as func/HOST/1234/9a0b6432 claimed 15, delivered 15, left 0 owed, into …

Delete the pickup directory afterwards — it holds addresses in clear, whichever runner filled it.
