/**
 * What an account can actually be debited for, right now.
 *
 * Today this is simply `balance`, and that is correct: `Account` in the API carries ONE money
 * column and nothing else — no authorisation holds, no overdraft limit, no pending ledger. So the
 * client's `amount <= availableBalanceOf(account)` is the exact complement of the server's
 * `if (account.Balance < request.Amount)` in TransactionService and TransferService.
 *
 * No decision record builds or rejects any of those three; the balance guard itself has no ADR, as
 * ADR-0050's Context says. No overdraft is the January 2026 design's intent ("Amount must be <=
 * current balance (NO OVERDRAFT)", docs/design/frontend-design/04a-ux-user-flows.md). The nearest
 * record is ADR-0050's "What would change this": a writer of `Pending` rows into `Transactions`
 * reopens its daily-limit `Completed` filter as a decision about reservations. A hold kept outside
 * `Transactions` would trip nothing written down. Until 2026-09-17 this comment said the
 * alternatives were "written down as future work in `Account.cs`'s trailing comment"; that list
 * named an overdraft limit but no hold and no pending ledger, and it is deleted.
 *
 * It exists as a named function anyway, because banking keeps "available" and "ledger" apart on
 * purpose — ISO 20022 gives them distinct balance-type codes, and this UI already writes
 * "Available:" under every amount field. The day a hold, an overdraft or a pending ledger arrives,
 * every outflow form starts silently over-permitting unless there is exactly one place to change.
 * This is that place, and the comment is here so nobody deletes it as a pointless wrapper.
 */
export function availableBalanceOf(account: { balance: number }): number {
  return account.balance;
}
