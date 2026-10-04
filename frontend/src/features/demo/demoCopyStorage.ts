import { z } from 'zod';
import { demoCopyInfoSchema } from '../../api/bffSchemas';
import type { DemoCopyInfo } from '../../api/bffTypes';
import { isDemoMode } from './demoMode';

/*
  What this browser keeps of a claimed demo copy: its sign-in details, under one key, so the
  visitor can come back to the copy.

  THE KEY IS THE SOURCE, and it is read each time the copy is asked for. Nothing here listens for
  what another tab does and nothing is loaded once and kept: a copy that another tab forgot or
  replaced is gone, or is the other one, the next time this tab asks. A copy held in a variable
  and loaded once would let a second open tab keep a password after the first was told "forget
  this copy".

  What is read is INPUT. The key holds a password, and anything on the page's origin can write to
  it, so what comes back is parsed and checked like an answer from the server, and a key that
  fails either is removed, not repaired.

  Off the demo the key is never read: a page that is not the demo has no business with it.

  A caught storage error is never logged. A browser with storage denied throws on every call, and
  that is a setting of the visitor's, not a fault to report.
*/
export const DEMO_COPY_STORAGE_KEY = 'azurebank.demoCopy';

/**
 * The kept shape: the five members of a claim's `copy`, checked by the same definition the
 * claim's answer is checked by, and the shape's number.
 *
 * `v` is exactly 1. A key written by a build that keeps another shape fails here and is removed,
 * which is what should happen to sign-in details this build cannot vouch for.
 */
export const storedDemoCopySchema = demoCopyInfoSchema.extend({ v: z.literal(1) });

export type StoredDemoCopy = z.infer<typeof storedDemoCopySchema>;

type Listener = () => void;

const listeners = new Set<Listener>();

/** The key's string at the last read, and the copy it parsed to: `null`, `null` for no key. */
let lastRaw: string | null = null;
let lastRead: StoredDemoCopy | null = null;

/**
 * The copy the browser would not store, kept for the life of the page. Not `null` only between a
 * write the browser refused and the next write it accepts, or a "forget".
 */
let refused: StoredDemoCopy | null = null;

function tellListeners(): void {
  for (const listener of listeners) listener();
}

function removeKey(): void {
  try {
    localStorage.removeItem(DEMO_COPY_STORAGE_KEY);
  } catch {
    // Storage denied: there is no key to remove, or none this page may touch.
  }
}

/** The copy a key's string holds, or `null` for a string that is not one. */
function parseStored(raw: string): StoredDemoCopy | null {
  try {
    const parsed = storedDemoCopySchema.safeParse(JSON.parse(raw));
    return parsed.success ? parsed.data : null;
  } catch {
    // Not JSON.
    return null;
  }
}

/**
 * Keeps a claimed copy, in place of whatever copy was kept before.
 *
 * The six members are written out one by one: what goes into the key is the kept shape and
 * nothing a caller happened to carry beside it.
 *
 * A browser that refuses the write still has the copy while the page lives: it is held here, and
 * it is what the page is given until a write succeeds or the copy is forgotten.
 */
export function writeDemoCopy(copy: DemoCopyInfo): void {
  const stored: StoredDemoCopy = {
    v: 1,
    email: copy.email,
    password: copy.password,
    pin: copy.pin,
    contacts: [...copy.contacts],
    expiresAt: copy.expiresAt,
  };
  try {
    localStorage.setItem(DEMO_COPY_STORAGE_KEY, JSON.stringify(stored));
    refused = null;
  } catch {
    refused = stored;
  }
  tellListeners();
}

/** Forgets the copy: the key, and the copy held for a browser that would not store it. */
export function forgetDemoCopy(): void {
  refused = null;
  removeKey();
  tellListeners();
}

/**
 * `useSyncExternalStore`'s subscribe. A listener is called after a write and after a "forget"
 * made through this module, on this page. It is not called for what another tab does: that is
 * read at this tab's next render, and not before.
 */
export function subscribeDemoCopy(listener: Listener): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}

/**
 * `useSyncExternalStore`'s snapshot: the kept copy, or `null`.
 *
 * Read from the key at each call, which costs one `getItem`. While the key's string is the one
 * read last time, the object handed back is the same one: a snapshot that was a new object at
 * every call would re-render its reader without end. A string that fails to parse, or parses to
 * something that is not a kept copy, removes the key.
 *
 * Nothing here looks at the copy's end. A copy past its end is still this browser's copy; who
 * offers it decides what an ended copy is worth.
 */
export function getDemoCopySnapshot(): StoredDemoCopy | null {
  // Off the demo there is no copy, and storage is not touched to find that out.
  if (!isDemoMode()) return null;
  // The browser refused the last write, so the key does not hold what this page was given. The
  // key may well be readable, and empty: it must not win over the copy the browser would not keep.
  if (refused) return refused;

  let raw: string | null;
  try {
    raw = localStorage.getItem(DEMO_COPY_STORAGE_KEY);
  } catch {
    // The browser refused the read. Nothing was read, so nothing is concluded about the key.
    return null;
  }
  if (raw === lastRaw) return lastRead;

  lastRead = raw === null ? null : parseStored(raw);
  lastRaw = lastRead === null ? null : raw;
  if (raw !== null && lastRead === null) removeKey();
  return lastRead;
}

/**
 * Is the signed-in user the owner of the copy this browser keeps? In the demo, and when the kept
 * copy's address is the user's, letter for letter: the two are the same string from the same
 * server, so nothing is folded or trimmed to make them meet.
 */
export function isDemoCopyOwner(
  copy: StoredDemoCopy | null,
  user: { email: string } | null,
): boolean {
  return isDemoMode() && copy !== null && user !== null && copy.email === user.email;
}

/** Tests only: the copy in memory, the last read and the key, so no test hands one to the next. */
export function __resetDemoCopy(): void {
  refused = null;
  lastRaw = null;
  lastRead = null;
  removeKey();
}
