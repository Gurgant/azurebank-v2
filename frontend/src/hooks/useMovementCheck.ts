import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import type { TransactionType } from '../api/enums';
import { liveUntil, type FoundMovement, type MovementReading } from '../api/movementByKey';
import type { ApiProblem } from '../api/problemBaseQuery';
import { useFindMovementByKeyMutation } from '../features/api/apiSlice';
import type { SentRequest } from './useIdempotentMutation';
import { useWaitLanding } from './useWaitLanding';

/** One account the listing is asked about, and the type of row the send writes there. */
export interface MovementTarget {
  readonly accountId: string;
  readonly type: TransactionType;
  /** A move's other account, whose balance the movement changed too. */
  readonly otherAccountId?: string;
}

/** Where a flow looks for what its send wrote: one account, and a second if that one is closed. */
export interface MovementTargets {
  readonly first: MovementTarget;
  /**
   * Asked when the first answers 403, which is a closed account: a move between the visitor's own
   * accounts wrote a row with the key on each, in one transaction.
   */
  readonly fallback?: MovementTarget;
}

/**
 * What the check view knows about the send whose answer was lost:
 * - `off`: nothing was left to ask about, and the view is today's;
 * - `looking`: the listing is being asked;
 * - `found`: the send wrote this movement, so it went through;
 * - `notYet`: not in the listing, asked while a send under the key could still commit;
 * - `none`: not in the listing, asked once nothing under the key can commit;
 * - `unchecked`: the listing could not answer. `canCheckAgain` is false for a closed account.
 */
export type MovementVerdict = 'off' | 'looking' | 'found' | 'notYet' | 'none' | 'unchecked';

export interface MovementCheck {
  verdict: MovementVerdict;
  /** The movement, when found: its amount, number and time, never its key. */
  movement: FoundMovement | null;
  /** Whether asking again could tell more: false once the account is known to be closed. */
  canCheckAgain: boolean;
  /** Nothing sent under the key can commit any more (`liveUntil`). */
  pastLive: boolean;
  /** The listing is being asked. */
  checking: boolean;
  /** The ask on screen, for `useWaitLanding`. */
  requestId: string;
  /** "Check again": asks the listing again, and lands focus on the primary button after it. */
  checkAgain: () => void;
  /** Whether "Check again" is drawn. */
  offerCheckAgain: boolean;
  /** Whether "Check again" is the primary button, ahead of "Check recent transactions". */
  checkAgainFirst: boolean;
  /** Whether the new-key action ("It didn't go through — …") is drawn. */
  offerNewKey: boolean;
  /** The ref for whichever button is the primary one: where a "Check again" lands. */
  landingRef: RefObject<HTMLButtonElement | null>;
}

/**
 * The actions the check view draws for a verdict, in one place for the three views that draw them.
 * "Check again" while asking can still tell more, and first while nothing under the key is known
 * to be past committing; the new-key action only once that is known, and the listing either found
 * nothing or could not answer.
 */
export function offersFor(verdict: MovementVerdict, canCheckAgain: boolean, pastLive: boolean) {
  const offerCheckAgain =
    verdict === 'looking' || verdict === 'notYet' || (verdict === 'unchecked' && canCheckAgain);
  return {
    offerCheckAgain,
    checkAgainFirst: verdict === 'looking' || (offerCheckAgain && !pastLive),
    offerNewKey: pastLive && (verdict === 'none' || verdict === 'unchecked'),
  };
}

type Settled =
  | { kind: 'off' }
  | { kind: 'looking' }
  | { kind: 'found'; movement: FoundMovement }
  | { kind: 'notYet' }
  | { kind: 'none' }
  | { kind: 'unchecked'; canCheckAgain: boolean };

interface CheckState<TBody> {
  /** The copy this state is about. A state about another copy is not read. */
  sent: SentRequest<TBody> | null;
  verdict: Settled;
  pastLive: boolean;
  asks: number;
}

const OFF: Settled = { kind: 'off' };
const LOOKING: Settled = { kind: 'looking' };

/**
 * Asks the listing by key what the send whose answer was lost wrote, and says what that means
 * while the check view is up (ADR-0022). Each flow calls it with its own copy, `lastSent`: the
 * flow owns the check step (ADR-0022, decision 4), and decides what a found movement lets go.
 *
 * It asks once when the copy arrives, again when the visitor presses "Check again", and once more
 * by itself at the instant `liveUntil` names, if the listing had not found it or could not answer
 * by then. An answer counts only when it is the latest ask's and about the copy still held: a late
 * answer about an older key, or one from an ask the copy's change aborted, is dropped. An empty
 * page is evidence only from an ask made at or after that instant, so the asks cannot loop.
 *
 * `target` and `onFound` are read when an answer comes, not when the hook was called, so a flow
 * may pass new functions on every render without asking again. `onFound` runs in the same tick
 * as the verdict.
 *
 * Under StrictMode the first run's ask is aborted by its own cleanup and its answer dropped; the
 * second run asks. Nothing guards that by hand, on purpose.
 */
export function useMovementCheck<TBody>(
  sent: SentRequest<TBody> | null,
  target: (body: TBody) => MovementTargets,
  onFound: (movement: FoundMovement) => void,
): MovementCheck {
  const [find] = useFindMovementByKeyMutation();
  const [state, setState] = useState<CheckState<TBody>>({
    sent: null,
    verdict: OFF,
    pastLive: false,
    asks: 0,
  });

  const targetRef = useRef(target);
  const onFoundRef = useRef(onFound);
  useEffect(() => {
    targetRef.current = target;
    onFoundRef.current = onFound;
  });

  const sentRef = useRef<SentRequest<TBody> | null>(null);
  const verdictRef = useRef<Settled>(OFF);
  const sequence = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const pending = useRef<{ abort: () => void } | null>(null);
  // When the latest ask was sent, on `performance.now()`'s clock.
  const lastAskedAt = useRef(-Infinity);

  const stop = useCallback(() => {
    if (timer.current !== null) clearTimeout(timer.current);
    timer.current = null;
    pending.current?.abort();
    pending.current = null;
    sequence.current += 1;
  }, []);

  /** Says `verdict` about `about`, unless the state has moved on to another copy. */
  const settle = useCallback((about: SentRequest<TBody>, verdict: Settled, pastLive = false) => {
    verdictRef.current = verdict;
    setState((previous) =>
      previous.sent === about
        ? { ...previous, verdict, pastLive: previous.pastLive || pastLive }
        : previous,
    );
  }, []);

  const ask = useCallback(
    (copy: SentRequest<TBody>) => {
      function askOnce(about: SentRequest<TBody>) {
        pending.current?.abort();
        const mine = ++sequence.current;
        const bound = liveUntil(about);
        const askedAt = performance.now();
        lastAskedAt.current = askedAt;
        verdictRef.current = LOOKING;
        // The first ask about a copy starts its state: what was said about another is not kept.
        setState((previous) =>
          previous.sent === about
            ? { ...previous, verdict: LOOKING, asks: previous.asks + 1 }
            : { sent: about, verdict: LOOKING, pastLive: askedAt >= bound, asks: 1 },
        );
        const targets = targetRef.current(about.body);
        const stillAsked = (key: string) =>
          mine === sequence.current && sentRef.current?.key === key;

        /*
          An ask sent before the bound and answered after it, with nothing found, asks once more
          at once. That ask is sent after the bound, so it cannot do the same again.
        */
        const conclude = (verdict: Settled) => {
          const now = performance.now();
          settle(about, verdict, now >= bound);
          const crossed = askedAt < bound && now >= bound;
          if (crossed && (verdict.kind === 'notYet' || verdict.kind === 'unchecked')) {
            askOnce(about);
          }
        };

        const send = (where: MovementTarget, isFallback: boolean) => {
          const request = find({
            idempotencyKey: about.key,
            accountId: where.accountId,
            type: where.type,
            ...(where.otherAccountId ? { otherAccountId: where.otherAccountId } : {}),
          });
          pending.current = request;
          const askedKey = request.arg.originalArgs.idempotencyKey;
          request.unwrap().then(
            (reading: MovementReading) => {
              if (!stillAsked(askedKey)) return;
              pending.current = null;
              if (reading.kind === 'found') {
                if (timer.current !== null) clearTimeout(timer.current);
                timer.current = null;
                settle(about, { kind: 'found', movement: reading.movement });
                onFoundRef.current(reading.movement);
              } else if (reading.kind === 'none') {
                conclude(askedAt >= bound ? { kind: 'none' } : { kind: 'notYet' });
              } else {
                conclude({ kind: 'unchecked', canCheckAgain: true });
              }
            },
            (error: unknown) => {
              if (!stillAsked(askedKey)) return;
              pending.current = null;
              // A 403 is an account outside the visitor's open ones: closed under the view.
              if ((error as Partial<ApiProblem> | undefined)?.status === 403) {
                if (!isFallback && targets.fallback) send(targets.fallback, true);
                else conclude({ kind: 'unchecked', canCheckAgain: false });
                return;
              }
              // Every other failure, a 401 among them: the app's own sign-out runs on that one.
              conclude({ kind: 'unchecked', canCheckAgain: true });
            },
          );
        };

        send(targets.first, false);
      }
      askOnce(copy);
    },
    [find, settle],
  );

  // What was said about another copy is not read: until its first ask, this one is looked for.
  const fresh = sent !== null && state.sent === sent;
  const verdict: Settled = fresh ? state.verdict : sent === null ? OFF : LOOKING;
  const pastLive = fresh && state.pastLive;
  const asks = fresh ? state.asks : 0;

  const { landingRef, arm } = useWaitLanding<HTMLButtonElement>(
    verdict.kind === 'looking',
    String(asks),
  );

  useEffect(() => {
    sentRef.current = sent;
    verdictRef.current = sent === null ? OFF : LOOKING;
    if (sent === null) return;
    const bound = liveUntil(sent);
    const now = performance.now();
    if (now < bound) {
      timer.current = setTimeout(() => {
        timer.current = null;
        setState((previous) =>
          previous.sent === sent ? { ...previous, pastLive: true } : previous,
        );
        const kind = verdictRef.current.kind;
        // While an ask is out, its answer decides (above); and an ask already sent at or after
        // the bound has said what there is to say.
        if ((kind === 'notYet' || kind === 'unchecked') && lastAskedAt.current < bound) {
          /*
            The ask disables "Check again", and a browser hands the focus of a disabled button
            to `body`. A visitor who waited on the view's first button gets it back once the
            answer is drawn, as after a press; focus anywhere else is theirs, and stays.
          */
          if (document.activeElement === landingRef.current) arm();
          ask(sent);
        }
      }, bound - now);
    }
    ask(sent);
    return stop;
  }, [sent, ask, stop, arm, landingRef]);

  const canCheckAgain = verdict.kind !== 'unchecked' || verdict.canCheckAgain;

  return {
    verdict: verdict.kind,
    movement: verdict.kind === 'found' ? verdict.movement : null,
    canCheckAgain,
    pastLive,
    checking: verdict.kind === 'looking',
    requestId: String(asks),
    checkAgain: () => {
      const about = sentRef.current;
      if (about === null || verdictRef.current.kind === 'looking') return;
      arm();
      ask(about);
    },
    ...offersFor(verdict.kind, canCheckAgain, pastLive),
    landingRef,
  };
}
