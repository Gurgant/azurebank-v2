/**
 * What an account can actually be debited for, right now.
 *
 * Today this is simply `balance`, and that is correct: `Account` in the API carries ONE money
 * column and nothing else — no authorisation holds, no overdraft limit, no pending ledger. The
 * alternatives are written down as future work in `Account.cs`'s trailing comment, not implemented.
 * So the client's `amount <= availableBalanceOf(account)` is the exact complement of the server's
 * `if (account.Balance < request.Amount)` in TransactionService and TransferService.
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
