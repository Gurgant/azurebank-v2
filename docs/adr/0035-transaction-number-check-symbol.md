# ADR-0035: A check symbol on the transaction number

**Status:** Accepted · **Date:** 2026-08-09

## Context

`TransactionNumber` is the identifier a person reads: it goes on a receipt, into a support ticket
and down a phone line. Two earlier changes addressed the machine's problem with it: the random
suffix went from six digits to seven Crockford base-32 characters, and a collision on the unique
index became recoverable. Neither addresses the failure a human produces: a mistyped number is
indistinguishable from a correct one, so a lookup finds nothing or, worse, a different real
transaction. ISO 11649's RF Creditor Reference and IBAN carry mod-97 check digits for exactly
this reason. The format those changes left (`TXN-`, eight date digits, `-`, seven suffix
characters) filled its 20-character column exactly, so a check symbol needs a migration.

## Decision

1. **The format is `TXN-YYYYMMDD-XXXXXXXXXXC`, 24 characters: a 10-character Crockford base-32
   suffix and one check symbol**, because a migration is unavoidable anyway, and taking the suffix
   from 7 to 10 in the same change retires the collision question.
2. **The check symbol is the payload mod 37, written in Crockford's check alphabet: the 32 encoding
   symbols plus `*~$=U`**, because 37 is prime and larger than the alphabet, which makes the
   guarantee total and not statistical. A substitution shifts the residue by
   `weight × (a − b) mod 37`, never zero, so every substitution of one symbol by a different one is
   rejected. An adjacent swap shifts it by `31 × weight × (a − b) mod 37`, and 31, 32 and 37 are
   pairwise coprime, so every adjacent transposition of two different symbols is rejected.
3. **The symbol is told from the payload by its position, always the 24th character, not by its
   character class**, because only a residue of 32 to 36 lands on `*~$=U`: the other 32 residues
   return an ordinary encoding character.
4. **The date is inside the payload**, because a symbol over the random part alone would accept
   `TXN-20260114` mistyped as `TXN-20260115`.
5. **`IsValidTransactionNumber` normalises as Crockford specifies before it checks: case folded,
   `I` and `L` read as 1, `O` read as 0**, because those letters are absent from the encoding
   alphabet so that the commonest transcription mistake is no mistake at all.
6. **Input must be ASCII**, because normalisation runs before the alphabet check and any character
   that uppercases into the alphabet would be folded into a legal one: U+017F (long s) uppercases
   to `S`, and two different strings would validate as one number.

## Rejected

- Rejected: backfilling stored numbers, because `EnforceTransactionImmutability` forbids
  renumbering a saved transaction.
- Rejected: the same algorithm in the frontend mock, because a second copy in TypeScript would
  drift with nothing to catch it, and no frontend code parses the value: the contract types it as a
  plain string. The mock's fixtures are 24 characters, so every screen renders at real length.
- Rejected: widening `GenerateTransferReference` from six digits, because nothing calls it: its
  value reaches no DTO and no database. If it is ever stored, it needs this treatment first.

## Consequences

- A swap of the last suffix character with the check symbol is rejected too: it validates only
  when `2(R − val(a)) ≡ 0 (mod 37)`, and 37 is odd, so only when the two are the same character.
  When the symbol is one of `*~$=U`, the format gate rejects the swap before any arithmetic.
- A row written before the migration keeps one of two older shapes, and neither validates: 19
  characters (`TXN-`, the date, six digits) or 20 (a seven-character suffix). That is safe only
  because no endpoint accepts a transaction number as input. A lookup by number, if one is added,
  must handle both.
- Not covered: nothing calls the validator on the request path. It exists for a support tool and
  for that lookup.
- The migration's `Down` is lossy: narrowing the column back to 20 with a new number present
  raises a truncation error; it does not cut four characters off an identifier.
- Entropy is pinned by counting suffix characters in the format assertion, because sampling cannot
  see a shortened suffix: at 32¹⁰ values a day 20,000 draws expect 1.8e-7 duplicates, and 0.0058
  with seven characters. The draw test stays as a smoke test of the random source.
- Not covered: on a 375 px screen the number wraps onto two lines, at the date's hyphen; it is one
  line at 768 and 1280.

## Verified by

- `IdGeneratorTests`: `EverySingleCharacterSubstitutionIsRejected` and
  `EveryAdjacentTranspositionIsRejected` (exhaustive, each mutant well-formed before it is
  rejected), `ANumberRetypedWithCrockfordConfusablesStillValidates`, `ANonAsciiLookalikeIsRejected`.

## Related

ADR-0036.
