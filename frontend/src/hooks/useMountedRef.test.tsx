import { StrictMode, useLayoutEffect, type MutableRefObject } from 'react';
import { render, renderHook } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useMountedRef } from './useMountedRef';

describe('useMountedRef', () => {
  it('says the component is on screen while it is, and that it has gone once it is unmounted', () => {
    const { result, unmount } = renderHook(() => useMountedRef());

    expect(result.current.current).toBe(true);

    unmount();

    expect(result.current.current).toBe(false);
  });

  it('still says so under StrictMode, which mounts, cleans up and mounts the component again', () => {
    // A cleanup with no `true` set in the effect's body leaves it false after the second mount, and
    // every send the component then asks for is refused.
    const { result, unmount } = renderHook(() => useMountedRef(), { wrapper: StrictMode });

    expect(result.current.current).toBe(true);

    unmount();

    expect(result.current.current).toBe(false);
  });

  it('has cleared by the time the removal commit is done with the component, before any passive cleanup', () => {
    /*
      An answer that resolves between the commit that removes a component and the passive cleanups
      that follow it reads the ref in that gap. `unmount()` cannot show the difference: it flushes
      the passive cleanups before it returns, so the ref reads `false` either way. A layout cleanup
      of a CHILD can: React runs a deleted component's own layout cleanup first, then its children's,
      all in the commit, so what the child reads is what code running in the gap would read. With a
      plain effect the ref is still `true` there.
    */
    const read: boolean[] = [];

    function Child({ mounted }: { mounted: MutableRefObject<boolean> }) {
      useLayoutEffect(
        () => () => {
          read.push(mounted.current);
        },
        [mounted],
      );
      return null;
    }

    function Page() {
      const mounted = useMountedRef();
      return <Child mounted={mounted} />;
    }

    const { unmount } = render(<Page />);
    unmount();

    expect(read).toEqual([false]);
  });
});
