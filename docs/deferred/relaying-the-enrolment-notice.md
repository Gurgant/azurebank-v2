# Relaying the enrolment notice, and what this deployment still cannot do

When a transfer PIN is enrolled, and when an existing one is changed, the API records in the same
transaction that the account holder is owed a notice (ADR-0045, ADR-0047). A runner renders each
owed notice as a complete RFC 5322 message, addressed to the email held on the account, into a
pickup directory. There are three runners: the operator tool's `notify` verb, a hosted loop in the
API (ADR-0048) and the same sweep as an Azure Function (ADR-0051). `Notices:Runner` names the hosted
one and is `None` unless it is set. The template that deploys the demo does not set it, so on Azure
no hosted runner renders a notice.

The message reaches a directory on the machine that wrote it and nobody else. **Nothing here
sends.** This page is about what would send it, and why that is not built. Two rendered samples, and
how to produce one, are in [`docs/notices/`](../notices/README.md).

## What would close it

- **A relay that collects from the directory.** The file is the message a mail server expects:
  headers, a blank line, a body, CRLF throughout. Postfix's pickup daemon, the pickup folder of the
  IIS SMTP service and commercial gateways read that shape from a directory and hand it on. Pointing
  one at the directory closes the last hop with no change to this repository, as far as the
  collector is durable: the row is marked delivered when the file is published, so a collector that
  loses a file loses a notice that nothing renders again.
- **A transport that speaks to a relay.** The seam is one interface, `INoticeTransport`, with one
  implementation, `PickupDirectoryTransport`. A second one would hand the rendered notice to an SMTP
  host or to a provider's API. It needs a host or an account to send through and a credential: one
  more validated secret, set in every place the others are.
  `NothingOnTheApiSideCanSendMail_AndThisPinsTheLimit` refuses a dependency on `System.Net.Mail`,
  MailKit, MimeKit, SendGrid or Twilio in the API, Infrastructure, Shared and the Function: a
  transport built on one of them turns it red, and the test changes with this decision.
- **For a demonstration only, a local catcher** such as Mailpit on `127.0.0.1`, which shows the
  messages in a browser. Nothing outside the machine is reached: one more container for what the
  pickup directory already shows.

## What it would buy

Detection. Whoever holds a stolen session and the account password can enrol a PIN, and whoever
holds a session and the current PIN can change it (ADR-0047). The notice is the only thing that
reaches the account's owner, and today it reaches a file that a person has to pass on. A sending
relay turns that into seconds after the event, which is the property NIST SP 800-63B-4 §4.6 is
written to provide.

## Why not here

Sending needs a provider: a third party's service, paid for outside a demo, with a credential to
keep. And the relay is not the first gap. No address in the store is proved by its holder, the
account holds one address where §4.6 asks for at least two, no endpoint changes it, and no PIN reset
stands behind the contact the notice names. A relay built before those would mail strangers, or
deliver a notice whose remedy is incomplete to the only address there is.

## What would have to be true

These are the preconditions for "delivered" that ADR-0048 and ADR-0051 point to.

- **A provider account, and its credential** stored as one more validated secret.
- **Addresses the project may write to, each proved by its holder before a notice goes to it.** The
  seeded accounts and the demo's copies hold addresses at `example.com`, at `azurebank.example` and
  at a domain this project does not own, and a registered account holds whatever was typed. The
  column `EmailConfirmed` is not the proof: every path that creates a user writes `true`
  (registration, the seeder, the demo pool's builder), sign-in does not require it, and
  `NoticeDeliveryRun`, which every runner shares, reads `Email` without consulting it. A sending
  transport must not trust the column. The proof is the email-confirmation flow that ADR-0013 defers
  until email infrastructure exists.
- **A second notification address, and a way to change either,** in the data model, or ADR-0045
  amended to say that one address is the whole design.
- **A remedy behind the contact**: a PIN reset or a revocation flow. What the contact can do today
  is in [`pin-enrolment-repudiated.md`](../runbooks/pin-enrolment-repudiated.md).

When those hold, the change is a second `INoticeTransport`, registered in place of the pickup
directory in all three composition roots: the tool's, the API's and the Function's.
`NoticeTransportRegistrationTests` fails unless each root registers exactly one transport and the
three agree on its lifetime and implementation, because a host left behind would keep writing files.
ADR-0045 D7, delivery recorded on the row, is then the decision to reopen: "delivered" moves from
the row to the relay's own receipt.

## What exists, and where it stops

- **Delivery is at-least-once, and the lease does not make it once** (ADR-0048 D3). A claim stops
  two runners from holding a row at the same moment. It cannot stop a runner that hands a notice to
  the transport and dies before it marks the row: the lease lapses and another runner takes the row.
  With the pickup directory the second attempt is refused, because the file is there: nothing goes
  out twice, and the row stays owed beside its file until somebody moves the file or marks the row.
  With a sending transport the message goes out twice, unless the transport or the provider honours
  an idempotency key or the recipient removes duplicates. Until one of those exists a duplicate is
  an accepted outcome.
- **The Function is a rehearsal of a deployment, not a delivery** (ADR-0051): it writes the same
  file from another process, on one machine, against a storage emulator.
  [Its README](../../backend/src/AzureBank.Functions.NoticeRelay/README.md) says how to run it and
  what the rehearsal is worth.
