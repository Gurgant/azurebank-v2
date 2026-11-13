import { useLayoutEffect, useRef } from 'react';

/**
 * Whether the component that calls this is still on screen, for code that runs after an await.
 *
 * A money flow asks for its authorisation first and sends second, and the wait between them is as
 * long as the network. A visitor can leave in it, by the browser's Back or by a sign-out, and the
 * request that was already on its way is not stopped by the component going. What comes after the
 * await has nothing to show its answer in, so it reads this and sends nothing.
 *
 * It is a layout effect so that the ref is cleared in the commit that removes the component. The
 * cleanup of a plain effect runs after that commit, and an answer that resolves in the gap would
 * still find the component on screen.
 *
 * `true` is set in the body of the effect as well as `false` in its cleanup: under StrictMode a
 * component mounts, is cleaned up and mounts again, and a cleanup alone would leave it `false` for
 * good. Read it in a callback, never in render: it changes without a render.
 */
export function useMountedRef() {
  const mounted = useRef(false);

  useLayoutEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  return mounted;
}
