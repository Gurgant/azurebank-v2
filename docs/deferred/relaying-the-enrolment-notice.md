# Relaying the enrolment notice, and why this deployment cannot

When a transfer PIN is enrolled, the API records that the account holder is owed a notice, in the
same transaction as the enrolment, and the operator tool's `notify` verb renders each owed notice as
a complete RFC 5322 message addressed to the email held on the account, into a pickup directory the
operator names. ADR-0045 sets out the design and, deliberately, its limit: the message reaches a
directory on this machine and nobody else. Nothing here sends.

This document is about the thing that would send it, and why it is not here.

## What would close it

**A relay that collects from the directory.** The file the verb writes is the message a mail server
expects: headers, a blank line, a body, CRLF throughout. Postfix's pickup daemon, the IIS SMTP
service's pickup folder and a dozen commercial gateways read exactly this shape from a directory and
hand it on. Pointing one at the directory closes the last hop without changing a line of this
repository — to the extent the collector is durable. The row calls the notice delivered when the
file is published, so a collector that loses a file loses a notice nothing will render again; that
is ADR-0045 D7's reason to move "delivered" from the row to the relay's own receipt the day one
exists. The pickup directory was chosen because the file is already the message.

**Or a transport that speaks to a relay.** The seam is one interface, `INoticeTransport`, with one
implementation. A second one would hand the rendered notice to an SMTP host or a provider's API. The
BCL's `System.Net.Mail` is already present and unreferenced; a provider client is one package. Both
need a credential — a seventh validated secret, taught to the five places the other six live — and a
host or account to send through.

**For a demonstration only, a local relay.** Mailpit or a similar container listening on
`127.0.0.1` accepts SMTP and shows the messages in a browser. It would let a reader watch the notice
arrive somewhere. It would also be a demonstration of a demonstration: nothing outside the machine
is reached, and a second container the reader has to start is more machinery for the same sentence
the pickup directory already lets the ADR say.

## What it would buy

Detection. In the threat model — an attacker with a stolen session and the account password enrols
a PIN — the notice is the only thing that reaches the legitimate owner, and today it reaches them
only if somebody runs a verb and then moves a file. A relay turns "when the operator remembers" into
"within seconds of the enrolment", which is the property NIST SP 800-63B-4 §4.6 is written to
provide.

## Why not here

The project's rule: a control whose value depends on a process running unattended, on a third party
watching, or on a paid subscription is out of scope on its own, because there is nothing here to run
it. A relay is all three — it runs unattended, it is a third party's service, and outside a demo it
is paid for. The seeded accounts hold addresses at `example.com` and at a domain this project does
not own; a real relay pointed at that store would mail strangers.

And the relay is not the first gap. The account holds ONE self-asserted, never-validated address,
where §4.6 asks for at least two; no endpoint changes it; no PIN reset stands behind the contact the
notice tells the reader to use. Those are data-model and flow decisions that come before any
transport, and a relay built ahead of them would deliver a notice whose remedy is incomplete to the
only address that exists.

## What would have to be true

- Something in the deployment runs between sessions, or an account with a provider exists whose
  credential can be stored as the seventh secret — the same condition `anchoring-the-audit-trail.md`
  states for third-party time.
- The store holds addresses the project may write to.
- A second notification address, and a path to change either, exist in the data model — or the ADR
  is amended to say that one address is the whole design.
- The repudiation path has a remedy: a PIN reset or revocation flow behind the contact.

When those hold, the change is a second `INoticeTransport`, registered in the tool's composition
root in place of the pickup directory, and ADR-0045 D7 — delivery recorded on the row rather than in
the chain — is the decision to reopen, because "delivered" will then mean something.
