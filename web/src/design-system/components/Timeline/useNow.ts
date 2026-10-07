import { useSyncExternalStore } from 'react';

/**
 * A shared minute clock for relative times: every subscriber re-renders once a minute, and render stays pure
 * (no `Date.now()` during render). Pass a fixed `now` to components in tests and stories instead.
 */
const MINUTE = 60_000;

function minuteNow(): number {
  return Math.floor(Date.now() / MINUTE) * MINUTE;
}

let current = minuteNow();
const listeners = new Set<() => void>();
let timer: ReturnType<typeof setInterval> | null = null;

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  if (timer === null) {
    current = minuteNow();
    timer = setInterval(() => {
      current = minuteNow();
      for (const l of listeners) l();
    }, MINUTE);
  }
  return () => {
    listeners.delete(listener);
    if (listeners.size === 0 && timer !== null) {
      clearInterval(timer);
      timer = null;
    }
  };
}

/** Stable within a minute, so it is safe to read during render even before the first subscription. */
function getSnapshot(): number {
  if (timer === null) current = minuteNow();
  return current;
}

export function useNow(fixed?: Date): number {
  const live = useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
  return fixed ? fixed.getTime() : live;
}
