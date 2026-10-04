import { afterEach, describe, expect, it, vi } from 'vitest';
import { enableDemoMode, rememberDemoCopy, resetDemoMode } from '../../test/demoMode';
import {
  forgetDemoCopy,
  getDemoCopySnapshot,
  isDemoCopyOwner,
  subscribeDemoCopy,
  writeDemoCopy,
} from './demoCopyStorage';

/*
  What the browser keeps of a claimed demo copy, and how it is read back.

  Every test turns the demo on for itself, except the two whose names say the tag is off: off the
  demo the module answers "no copy" before it looks anywhere, so a test that forgot the tag would
  be asserting that and nothing else.

  Two ways of putting a copy in the browser are used, and they are not the same thing.
  `writeDemoCopy` is the product's own write. `rememberDemoCopy` and a bare
  `localStorage.removeItem` are raw: the module is not told and nobody who listens is called, which
  is what a reload finds and what another tab leaves behind.

  The key and the kept shape are typed out here, not imported from the module, so the module
  cannot move either and take this file with it. The two copies are the mock's first two
  (src/mocks/state.ts): fixtures, which no server knows.
*/
const KEY = 'azurebank.demoCopy';

const copy = {
  email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
  password: 'Xk7p-Rm3w-Hn8d-Tq5v',
  pin: '123456',
  contacts: ['jane_k7m2', 'mike_k7m2'],
  expiresAt: '2026-10-05T09:00:00.000Z',
};

const anotherCopy = {
  email: 'demo-4h9d2s7f1g6j3k8a@azurebank.example',
  password: 'Fb4t-Wy9c-Kz2g-Ne6s',
  pin: '123456',
  contacts: ['jane_p3x8', 'mike_p3x8'],
  expiresAt: '2026-10-06T10:30:00.000Z',
};

/** What the key holds for a copy: its five members and the shape's number. */
const kept = (claimed: typeof copy) => ({ v: 1, ...claimed });

/** The key as it is, with no module in between. `null` when the key is absent. */
function keyNow(): unknown {
  const raw = localStorage.getItem(KEY);
  return raw === null ? null : JSON.parse(raw);
}

function keysNow(): string[] {
  return Array.from({ length: localStorage.length }, (_, index) => localStorage.key(index) ?? '');
}

/** Makes the browser refuse one of storage's calls, as a browser with storage denied does. */
function deny(call: 'getItem' | 'setItem') {
  return vi.spyOn(Storage.prototype, call).mockImplementation(() => {
    throw new Error('SecurityError');
  });
}

/*
  Storage is put back after every test, whatever happened in it. Each test that denies a call
  restores it itself, at the moment the denial is over; but a test that failed before that line
  would hand its refusal to every test after it, and they would be red for a reason that is not
  theirs.
*/
afterEach(() => {
  vi.restoreAllMocks();
});

// Written by the test that leaves a copy behind on purpose, read by the one after it.
let theTestBeforeLeft = { aKey: false, aCopyInMemory: false };

describe('the demo copy the browser keeps', () => {
  it('keeps a claimed copy under one key, in one shape', () => {
    enableDemoMode();
    // One member more than a copy has: what is kept is the six members and nothing beside them.
    const claimed = { ...copy, somethingElse: 'not kept' };

    writeDemoCopy(claimed);

    expect({ keys: keysNow(), kept: keyNow() }).toStrictEqual({
      keys: ['azurebank.demoCopy'],
      kept: {
        v: 1,
        email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example',
        password: 'Xk7p-Rm3w-Hn8d-Tq5v',
        pin: '123456',
        contacts: ['jane_k7m2', 'mike_k7m2'],
        expiresAt: '2026-10-05T09:00:00.000Z',
      },
    });
  });

  it('gives back what a reload finds', () => {
    enableDemoMode();
    rememberDemoCopy(copy);

    expect(getDemoCopySnapshot()).toStrictEqual(kept(copy));
  });

  it('removes what it cannot read', () => {
    enableDemoMode();
    const noPassword = {
      v: 1,
      email: copy.email,
      pin: copy.pin,
      contacts: copy.contacts,
      expiresAt: copy.expiresAt,
    };
    const unreadable: Record<string, string> = {
      anotherShapeNumber: JSON.stringify({ ...copy, v: 2 }),
      noShapeNumber: JSON.stringify(copy),
      aMemberMissing: JSON.stringify(noPassword),
      notJson: '{"v":1,"email":',
      notAnObject: JSON.stringify('demo-k7m2x9q4w8e1r5t3@azurebank.example'),
      jsonNull: 'null',
      anEndThatIsNotAnInstant: JSON.stringify({ ...kept(copy), expiresAt: 'tomorrow' }),
      anEndThatNamesNoZone: JSON.stringify({ ...kept(copy), expiresAt: '2026-10-05T09:00:00' }),
    };

    // Each case is put in the key raw, asked for, and the key read again. The whole copy goes
    // through the same steps first and last, so a "removed" is an answer about that case.
    const ask = (raw: string) => {
      localStorage.setItem(KEY, raw);
      const snapshot = getDemoCopySnapshot();
      return { snapshot, keyAfter: localStorage.getItem(KEY) };
    };
    const whole = JSON.stringify(kept(copy));
    const seen = {
      wholeAtFirst: ask(whole),
      ...Object.fromEntries(Object.entries(unreadable).map(([name, raw]) => [name, ask(raw)])),
      wholeAtLast: ask(whole),
    };

    const removed = { snapshot: null, keyAfter: null };
    expect(seen).toStrictEqual({
      wholeAtFirst: { snapshot: kept(copy), keyAfter: whole },
      anotherShapeNumber: removed,
      noShapeNumber: removed,
      aMemberMissing: removed,
      notJson: removed,
      notAnObject: removed,
      jsonNull: removed,
      anEndThatIsNotAnInstant: removed,
      anEndThatNamesNoZone: removed,
      wholeAtLast: { snapshot: kept(copy), keyAfter: whole },
    });
  });

  it('forgets on request, and tells whoever listens', () => {
    enableDemoMode();
    rememberDemoCopy(copy);
    const before = getDemoCopySnapshot();
    const listener = vi.fn();
    const stopListening = subscribeDemoCopy(listener);

    forgetDemoCopy();
    stopListening();

    expect({
      before,
      key: keyNow(),
      snapshot: getDemoCopySnapshot(),
      told: listener.mock.calls.length,
    }).toStrictEqual({ before: kept(copy), key: null, snapshot: null, told: 1 });
  });

  it('tells whoever listens when a copy is written, and nobody who stopped listening', () => {
    enableDemoMode();
    const listener = vi.fn();
    const gone = vi.fn();
    const stopListening = subscribeDemoCopy(listener);
    subscribeDemoCopy(gone)();

    writeDemoCopy(copy);
    stopListening();

    expect({
      told: listener.mock.calls.length,
      toldTheOneWhoLeft: gone.mock.calls.length,
    }).toStrictEqual({ told: 1, toldTheOneWhoLeft: 0 });
  });

  it('hands back the same object until something changes', () => {
    enableDemoMode();
    rememberDemoCopy(copy);

    const first = getDemoCopySnapshot();
    const second = getDemoCopySnapshot();

    // The first clause is what keeps the second from being "nothing, twice".
    expect(first).toStrictEqual(kept(copy));
    expect(second).toBe(first);
  });

  it('what another tab replaced is what is read', () => {
    enableDemoMode();
    writeDemoCopy(copy);
    const before = getDemoCopySnapshot();

    // The other tab: the key rewritten, this page's module not told.
    rememberDemoCopy(anotherCopy);

    expect({ before, after: getDemoCopySnapshot() }).toStrictEqual({
      before: kept(copy),
      after: kept(anotherCopy),
    });
  });

  it('what another tab forgot is gone at the next read', () => {
    enableDemoMode();
    writeDemoCopy(copy);
    const before = getDemoCopySnapshot();

    // The other tab: the key removed, this page's module not told.
    localStorage.removeItem(KEY);

    expect({ before, after: getDemoCopySnapshot() }).toStrictEqual({
      before: kept(copy),
      after: null,
    });
  });

  it('with the tag off nothing is read', () => {
    rememberDemoCopy(copy);
    const whole = localStorage.getItem(KEY);
    const reads = vi.spyOn(Storage.prototype, 'getItem');
    const readsOfTheKey = () => reads.mock.calls.filter(([key]) => key === KEY).length;

    const whileOff = getDemoCopySnapshot();
    const readsWhileOff = readsOfTheKey();
    // The same question with the tag on, so the silence above is this spy's to report: it sees
    // the read the moment there is one.
    enableDemoMode();
    const whileOn = getDemoCopySnapshot();
    const readsWhileOn = readsOfTheKey();
    reads.mockRestore();

    expect({ whileOff, readsWhileOff, whileOn, readsWhileOn }).toStrictEqual({
      whileOff: null,
      readsWhileOff: 0,
      whileOn: kept(copy),
      readsWhileOn: 1,
    });
    expect(localStorage.getItem(KEY)).toBe(whole);
  });

  it('with the tag off a copy the browser would not store is not handed back either', () => {
    const denied = deny('setItem');
    writeDemoCopy(copy);
    denied.mockRestore();

    const whileOff = getDemoCopySnapshot();
    // The same question with the tag on: the copy was there to be handed back.
    enableDemoMode();

    expect({ whileOff, whileOn: getDemoCopySnapshot() }).toStrictEqual({
      whileOff: null,
      whileOn: kept(copy),
    });
  });

  it('a browser that refuses to store still remembers the copy while the page lives', () => {
    enableDemoMode();
    // Not stubbed: a spy that calls through, so anything logged still reaches the suite's own gate
    // (src/test/setup.ts). A storage that throws is caught and is nobody's error to read.
    const logged = vi.spyOn(console, 'error');
    const listener = vi.fn();
    const stopListening = subscribeDemoCopy(listener);
    const denied = deny('setItem');

    expect(() => writeDemoCopy(copy)).not.toThrow();
    const first = getDemoCopySnapshot();
    // The key can be read and holds nothing: the browser refused the write. It must not win over
    // what the browser refused to keep.
    const second = getDemoCopySnapshot();
    denied.mockRestore();
    stopListening();

    expect({
      first,
      key: keyNow(),
      told: listener.mock.calls.length,
      logged: logged.mock.calls.length,
    }).toStrictEqual({ first: kept(copy), key: null, told: 1, logged: 0 });
    expect(second).toBe(first);
    logged.mockRestore();
  });

  it('…until a write succeeds, and then the key is what is read again', () => {
    enableDemoMode();
    const denied = deny('setItem');
    writeDemoCopy(copy);
    denied.mockRestore();
    const refused = getDemoCopySnapshot();

    writeDemoCopy(anotherCopy);
    const written = getDemoCopySnapshot();
    const keyOnceWritten = keyNow();
    // The other tab again. Were the copy in memory still the answer, this would change nothing.
    localStorage.removeItem(KEY);

    expect({ refused, written, keyOnceWritten, onceRemoved: getDemoCopySnapshot() }).toStrictEqual({
      refused: kept(copy),
      written: kept(anotherCopy),
      keyOnceWritten: kept(anotherCopy),
      onceRemoved: null,
    });
  });

  it('…or until it is forgotten', () => {
    enableDemoMode();
    const denied = deny('setItem');
    writeDemoCopy(copy);
    denied.mockRestore();
    const refused = getDemoCopySnapshot();

    forgetDemoCopy();
    const forgotten = getDemoCopySnapshot();
    // What a reload or another tab leaves afterwards is read from the key, as before the refusal.
    rememberDemoCopy(anotherCopy);

    expect({ refused, forgotten, afterwards: getDemoCopySnapshot() }).toStrictEqual({
      refused: kept(copy),
      forgotten: null,
      afterwards: kept(anotherCopy),
    });
  });

  it('a write the browser refuses does not leave the copy before it in the key', () => {
    enableDemoMode();
    writeDemoCopy(copy);
    const keyBefore = keyNow();
    const denied = deny('setItem');
    writeDemoCopy(anotherCopy);
    denied.mockRestore();

    // The copy before it is not this browser's copy any more. Left in the key, it is what a
    // reload would read back, with its password.
    expect({ keyBefore, snapshot: getDemoCopySnapshot(), key: keyNow() }).toStrictEqual({
      keyBefore: kept(copy),
      snapshot: kept(anotherCopy),
      key: null,
    });
  });

  it('a browser that refuses to be read has no copy, and nothing throws', () => {
    enableDemoMode();
    rememberDemoCopy(copy);
    // Read once before the refusal: a page that had the copy a moment ago is the page a refused
    // read must not leave holding it.
    const beforeTheRefusal = getDemoCopySnapshot();
    const denied = deny('getItem');

    let whileRefused: unknown = 'not asked';
    expect(() => {
      whileRefused = getDemoCopySnapshot();
    }).not.toThrow();
    denied.mockRestore();

    // A read the browser refused is not a copy that could not be read: the key is left as it was,
    // and is the copy again the moment the browser answers.
    expect({ beforeTheRefusal, whileRefused, onceItAnswers: getDemoCopySnapshot() }).toStrictEqual({
      beforeTheRefusal: kept(copy),
      whileRefused: null,
      onceItAnswers: kept(copy),
    });
  });

  it('the owner is the signed-in user whose address the stored copy carries, in the demo', () => {
    enableDemoMode();
    rememberDemoCopy(copy);
    const stored = getDemoCopySnapshot();
    const owner = { email: 'demo-k7m2x9q4w8e1r5t3@azurebank.example' };

    const inTheDemo = {
      theSameAddress: isDemoCopyOwner(stored, owner),
      anotherAddress: isDemoCopyOwner(stored, { email: 'demo-4h9d2s7f1g6j3k8a@azurebank.example' }),
      // Exact equality: the address is compared as it is written, not as a mailbox would read it.
      theSameAddressInCapitals: isDemoCopyOwner(stored, { email: owner.email.toUpperCase() }),
      noCopy: isDemoCopyOwner(null, owner),
      nobodySignedIn: isDemoCopyOwner(stored, null),
    };
    resetDemoMode();
    const withTheTagOff = isDemoCopyOwner(stored, owner);

    expect({ stored, ...inTheDemo, withTheTagOff }).toStrictEqual({
      stored: kept(copy),
      theSameAddress: true,
      anotherAddress: false,
      theSameAddressInCapitals: false,
      noCopy: false,
      nobodySignedIn: false,
      withTheTagOff: false,
    });
  });

  /*
    The next two tests are a pair and their order is the point. The first leaves a copy behind in
    both places the module can hold one, on purpose: one in the key, and another in memory, which
    is where a copy goes when the browser refuses the write. Nothing in this file clears either,
    so the second can only be green because `setup.ts` did.
  */
  it('leaves a copy in the key and another in memory', () => {
    enableDemoMode();
    const denied = deny('setItem');
    writeDemoCopy(copy);
    denied.mockRestore();
    // Put in the key after the refusal, as another tab would: a refused write leaves no key.
    rememberDemoCopy(anotherCopy);

    theTestBeforeLeft = {
      aKey: localStorage.getItem(KEY) !== null,
      aCopyInMemory: getDemoCopySnapshot() !== null,
    };

    expect(getDemoCopySnapshot()).toStrictEqual(kept(copy));
    expect(keyNow()).toStrictEqual(kept(anotherCopy));
    // Left there, on purpose.
  });

  it('a copy left by one test is gone for the next', () => {
    enableDemoMode();

    expect({
      theTestBeforeLeft,
      key: keyNow(),
      snapshot: getDemoCopySnapshot(),
    }).toStrictEqual({
      theTestBeforeLeft: { aKey: true, aCopyInMemory: true },
      key: null,
      snapshot: null,
    });
  });
});
