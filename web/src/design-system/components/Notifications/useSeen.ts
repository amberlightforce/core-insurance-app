import { useEffect, useRef, useState, type RefObject } from 'react';

/** MI-60: an item counts as seen after it has been visible for this long. */
export const seenAfterMs = 1000;

/**
 * Calls `onSeen` once the element has been at least half visible for `seenAfterMs` (IntersectionObserver;
 * without it the element counts as visible). Returns true from then on.
 */
export function useSeen(
  ref: RefObject<Element | null>,
  isActive: boolean,
  onSeen?: () => void,
): boolean {
  const [seen, setSeen] = useState(false);
  const latest = useRef(onSeen);
  useEffect(() => {
    latest.current = onSeen;
  });

  useEffect(() => {
    const el = ref.current;
    if (!isActive || seen || !el) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const start = () => {
      timer ??= setTimeout(() => {
        setSeen(true);
        latest.current?.();
      }, seenAfterMs);
    };
    const stop = () => {
      clearTimeout(timer);
      timer = undefined;
    };
    if (typeof IntersectionObserver === 'undefined') {
      start();
      return stop;
    }
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((entry) => entry.isIntersecting)) start();
        else stop();
      },
      { threshold: 0.5 },
    );
    observer.observe(el);
    return () => {
      observer.disconnect();
      stop();
    };
  }, [ref, isActive, seen]);

  return seen;
}
