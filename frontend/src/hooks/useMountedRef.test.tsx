import { StrictMode } from 'react';
import { renderHook } from '@testing-library/react';
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
});
