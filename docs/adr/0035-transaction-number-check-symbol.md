# ADR-0035: A check symbol on the transaction number

**Status:** Accepted · **Date:** 2026-08-09 · **Supersedes nothing.** Closes the residual left open
by PR #89 (widening) and PR #90 (collision recovery).

## Context

`TransactionNumber` is the identifier a person reads. It goes on a receipt, into a support ticket,
and down a phone line. The last two changes to it both addressed the machine's problem with it:

- **PR #89** widened the random suffix from six digits to seven Crockford base-32 characters,
  because 900,000 values per UTC day reaches even odds of collision at about 1,100 transactions a
  day and the column carries a unique index — a collision is a failed INSERT on a money endpoint.
- **PR #90** made that failure recoverable rather than a raw 500, narrowed by index name so it could
  not be confused with the idempotency claim race.

Neither addressed the failure a **human** produces. A mistyped transaction number is
indistinguishable from a correct one: nothing about the string says it is wrong, so a lookup either
finds nothing or — worse — finds a **different real transaction**. Being silently wrong about which
transaction someone is asking about is a bad failure for a bank, and it is far more likely than the
collision the previous two PRs were about.

The retail world solves this the same way. ISO 11649's RF Creditor Reference carries mod-97 check
digits for exactly this reason; IBAN does the same.

## What was measured first

The old format filled its column **exactly**: `TXN-` + 8 date digits + `-` + 7 suffix characters =
20, and `ValidationRules.TransactionNumberLength` was 20. There was no spare character, so a check
symbol could not be added without a migration. That fact — not the entropy — is what decided the
shape of this change: since a migration was unavoidable, the suffix was taken to 10 at the same
time, which retires the collision question instead of deferring it a third time.

Observed on the migrated LocalDB after the SQL proofs ran:

```
TransactionNumber   nvarchar   24

TXN-20260808-XSTHEQF737*   24
TXN-20260808-FRMAX8TENFQ   24
TXN-20260808-17SYTVSGMTM   24
```

The emitted migration SQL was inspected rather than assumed. Because the column carries
`IX_Transactions_TransactionNumber`, the provider drops the unique index, runs the `ALTER`, and
recreates it — SQL Server refuses to alter an indexed column otherwise.

## Decision

The format becomes `TXN-YYYYMMDD-XXXXXXXXXXC`, 24 characters: a 10-character Crockford base-32
suffix followed by **one check symbol** computed over the date and the suffix together.

**Mod 37 over the base-32 payload**, mapped to Crockford's check alphabet (the 32 encoding symbols
plus `*~$=U`, five that can never appear in the payload so the symbol is always separable from the
value it protects).

The modulus is what makes the guarantee **total rather than statistical**. 37 is prime and larger
than the alphabet, so:

- a single substituted character shifts the residue by `weight × (a − b) mod 37`, which is never
  zero — **every** substitution of one symbol for a **different** symbol is rejected;
- swapping two adjacent characters shifts it by `31 × (a − b) mod 37`, and `gcd(31, 37) = 1` —
  **every** adjacent transposition is rejected.

Read "different symbol" strictly, because Crockford's aliases are decoded **before** the check runs:
writing `O` where the value holds `0`, or `I`/`L` where it holds `1`, is not a substitution at all —
it is the same value spelled the way a person spells it, and `IsValidTransactionNumber` accepts it.
That is the point of removing those letters from the alphabet, not a hole in the guarantee, and it
has a test of its own rather than only this paragraph.

Both are asserted exhaustively rather than by example: `IdGeneratorTests` enumerates every
single-character substitution (382 per number, over 25 numbers) and every adjacent transposition,
and asserts each mutant is still structurally well-formed **before** asserting it is rejected — so a
mutation turned away by the format gate cannot be mistaken for one caught by the arithmetic.

The transposition that straddles the suffix and the check symbol is covered too, and by a different
argument, since the theorem above is about two characters inside the payload and the symbol is the
residue rather than payload. Working it through: with payload `P` ending in `a`, residue `R` and
symbol `c`, the swap produces a payload whose residue is `R + val(c) − val(a)`, and it validates
only when `2(R − val(a)) ≡ 0 (mod 37)`. 37 is odd, so 2 is invertible and that requires
`R = val(a)` — exactly the case where `c` and `a` are the same character and nothing moved. Every
real suffix/check transposition is therefore rejected as well.

The **date is inside the payload**. A symbol over the random part alone would wave through
`TXN-20260114` mistyped as `TXN-20260115`.

`IsValidTransactionNumber` normalises the way Crockford specifies before checking — case folded, I
and L read as 1, O read as 0. Those letters are absent from the encoding alphabet precisely so they
can be reinterpreted here rather than rejected, which turns the commonest transcription mistake into
no mistake at all.

## What this deliberately does not do

- **No backfill.** Rows written before `WidenTransactionNumberForCheckSymbol` keep their
  20-character numbers, and `IsValidTransactionNumber` answers **false** for them. That is safe only
  because nothing validates a stored number: no endpoint accepts a transaction number as input, and
  `EnforceTransactionImmutability` forbids renumbering a saved transaction. A future
  lookup-by-number endpoint must not gate on this without handling the legacy shape first.
- **Nothing calls the validator on the request path today.** It exists for a support tool and for
  that future lookup. Stated here so the next reader does not assume a validation step that is not
  wired up.
- **`GenerateTransferReference` is left at six digits.** The reason to widen does not apply: it
  carries no unique index, so a repeat is a cosmetic coincidence rather than a failed INSERT. If a
  uniqueness constraint is ever added, it needs this treatment first.
- **The frontend mock does not reimplement the symbol.** Its fixtures are 24 characters so every
  screen renders at production length, but the sequence stays readable rather than checksum-correct.
  A second copy of the algorithm in TypeScript would drift with nothing able to catch it, and would
  buy nothing — no frontend code parses this value; the contract types it as a plain string.

## Consequences

- **Down is lossy.** Narrowing the column back to 20 with any post-migration number present raises a
  truncation error rather than cutting four characters off an identifier. That is the correct
  behaviour, but it means the migration is not freely reversible once new numbers exist.
- **Sampling stopped being a usable oracle for entropy.** At 32¹⁰ per day, 20,000 draws expect
  1.8e-7 duplicates — and a reverted 7-character suffix expects 0.0058, so both come back clean. The
  draw test can no longer detect a shortened suffix and survives only as an RNG smoke test; the
  entropy is now pinned by counting suffix characters in the format assertion, which is exact.
  Recorded because the previous version of that test claimed a detection power it would no longer
  have.
- **On screen the number now wraps at 375px.** Measured on the transaction-detail page at three
  widths: two lines at 375, one line at 768 and 1280, no clipping and no horizontal page scroll at
  any of them. The break falls at the date's hyphen, so both groups stay intact. Left alone
  deliberately — the fix is a styling change, and UI/UX work is sequenced last.
