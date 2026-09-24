# The design corpus — history

Everything in this folder was written before or during the January 2026 build, and came into the
repository with the design history on 2026-07-12. It is kept as it was, for provenance, and every
document says so in a banner at its top. **None of it is instruction.** Where a document here
disagrees with the code or with the generated contract,
[`docs/api/openapiv1.json`](../api/openapiv1.json), the code and the contract win.

[`00-DESIGN-SOURCE-OF-TRUTH.md`](00-DESIGN-SOURCE-OF-TRUTH.md) maps the folder as it stood in
July 2026, with a dated correction of what has changed under it since.

## Where the corpus and the code disagree

These are the divergences known to have misled a reader, or likely to, measured on 2026-09-23. The
list is not complete; that is what the banners are for.

- **Routes that do not exist.** Nine are published here. `GET /api/users/search` was deleted on
  2026-07-17 (ADR-0014) — and the Bruno collection was still calling it until #196. The other eight
  never existed in any controller, on any branch: `/api/recipients/search`,
  `/api/recipients/recent`, `/api/recipients/{id}`, `/api/recipients/validate/{…}`, `/api/payees`,
  `POST /api/transfers/external`, `/api/accounts/{id}/primary` (the real route is
  `PATCH /api/accounts/{id}/set-primary`) and `/api/auth/session` (the BFF has
  `GET /bff/auth/session-status`).
- **The error envelope.** `06-api-contracts.md` publishes `{type, message, correlationId,
  statusCode, errors, details}`. The API answers RFC 9457 problem details; see the note at that
  document's section 6.1.
- **Login's token.** `06` and `07` show login answering a token object, `04e` and `11` a bare
  string. The API answered the string until 2026-09-23 and the object since (#202).
- **Account types.** `06` writes them lowercase, `"checking"` and `"savings"`, on eight lines. The
  contract's enum is `Checking`, `Savings`, `Investment`.
