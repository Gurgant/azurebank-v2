/** `'all'` is a state, not an id, so a stale selection can only ever fall back to the total. */
export type Scope = 'all' | string;

/**
 * The scope as a QUERY argument: the picked account, but only while it still exists.
 *
 * Exported and pure because the inline version could not be falsified. Written in the component it
 * was covered by a test that selected an account, removed it from the seed and re-rendered — and
 * that test passed with the check REMOVED, because a second `render` mounts a fresh component whose
 * `scope` is back to `'all'`. The stale state it claimed to exercise never existed.
 *
 * The rule itself is what `Scope`'s comment above always promised and only the label delivered:
 * `selected` is a `find`, so it fell back on its own, while the two queries sent the id whatever the
 * accounts list said. An account removed from under a mounted page therefore produced a card
 * labelled for every account beside a request the server answers 403 to.
 */
export function resolveScopedAccountId(
  scope: Scope,
  accounts: readonly { id: string }[],
): string | undefined {
  return scope !== 'all' && accounts.some((a) => a.id === scope) ? scope : undefined;
}
