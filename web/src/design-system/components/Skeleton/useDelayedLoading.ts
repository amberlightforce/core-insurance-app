import { useEffect, useRef, useState } from 'react';

import { durations } from '../../tokens';

export interface DelayedLoadingOptions {
  /** Nothing shows for fast loads under this delay (ms). Default `durations.delaySkeleton` (150). */
  delay?: number;
  /** Once shown, the placeholder stays at least this long (ms) so it never flashes. Default 300. */
  minDisplay?: number;
}

/**
 * Skeleton timing (Part 2 §4.24, A.11): returns true while a loading placeholder should be visible.
 * The placeholder appears only after `delay` and, once visible, stays for at least `minDisplay`, so fast
 * responses show nothing and slow ones never flicker.
 */
export function useDelayedLoading(
  isLoading: boolean,
  {
    delay = durations.delaySkeleton,
    minDisplay = durations.minDisplaySkeleton,
  }: DelayedLoadingOptions = {},
): boolean {
  const [visible, setVisible] = useState(false);
  const shownAt = useRef(0);

  useEffect(() => {
    if (isLoading && !visible) {
      const id = setTimeout(() => {
        shownAt.current = Date.now();
        setVisible(true);
      }, delay);
      return () => {
        clearTimeout(id);
      };
    }
    if (!isLoading && visible) {
      const remaining = Math.max(0, minDisplay - (Date.now() - shownAt.current));
      const id = setTimeout(() => {
        setVisible(false);
      }, remaining);
      return () => {
        clearTimeout(id);
      };
    }
    return undefined;
  }, [isLoading, visible, delay, minDisplay]);

  return visible;
}
