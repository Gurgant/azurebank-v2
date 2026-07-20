import { useEffect, useRef, useState } from 'react';
import { Text } from '@fluentui/react-components';

function formatRemaining(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}:${String(seconds % 60).padStart(2, '0')}`;
}

interface RetryCountdownProps {
  /** Absolute epoch-ms deadline — build it with retryDeadline(retryAfterSeconds). */
  deadline: number;
  /** Fired once when the countdown reaches zero (re-enable the blocked action). */
  onElapsed?: () => void;
}

/**
 * The one 429 primitive (D13). Placement and surrounding copy belong to the owning
 * surface (inline under a submit, login lock message, PIN dialog); this only ticks
 * down to the deadline, announces politely, and fires onElapsed once.
 */
export function RetryCountdown({ deadline, onElapsed }: RetryCountdownProps) {
  const [now, setNow] = useState(() => Date.now());
  const onElapsedRef = useRef(onElapsed);

  useEffect(() => {
    onElapsedRef.current = onElapsed;
  }, [onElapsed]);

  useEffect(() => {
    if (deadline <= Date.now()) {
      onElapsedRef.current?.();
      return;
    }
    const timer = setInterval(() => {
      setNow(Date.now());
      if (deadline <= Date.now()) {
        clearInterval(timer);
        onElapsedRef.current?.();
      }
    }, 1000);
    return () => clearInterval(timer);
  }, [deadline]);

  const remaining = Math.max(0, Math.ceil((deadline - now) / 1000));
  if (remaining <= 0) return null;

  return (
    <Text role="timer" aria-live="polite">
      Try again in {formatRemaining(remaining)}
    </Text>
  );
}
