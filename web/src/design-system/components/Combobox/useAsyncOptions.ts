import { useCallback, useEffect, useState } from 'react';

import { durations } from '../../tokens';

/** Loads options for a query; reject (or throw) to show the error state. Honour `signal` to cancel. */
export type LoadOptions<T> = (query: string, signal: AbortSignal) => Promise<readonly T[]>;

export type AsyncStatus = 'idle' | 'tooShort' | 'loading' | 'ready' | 'error';

export interface AsyncOptions<T> {
  status: AsyncStatus;
  /** The latest results (kept, dimmed, while the next query loads). */
  items: readonly T[];
  /** True while older results are shown for a newer query. */
  isStale: boolean;
  /** The 2 px loading bar shows only after 150 ms of loading. */
  showBar: boolean;
  /** The minimum query length for the current query (2, or 1 for numeric IDs). */
  minLength: number;
  retry: () => void;
}

/** Debounce before a request (Part 2 §4.4). */
export const ASYNC_DEBOUNCE_MS = 150;

interface Result<T> {
  key: string;
  items: readonly T[];
  failed: boolean;
}

export function minQueryLengthFor(query: string, minLength?: number): number {
  if (minLength !== undefined) return minLength;
  return /^\d+$/.test(query) ? 1 : 2;
}

/**
 * Async options for Combobox: 150 ms debounce, minimum 2 characters (1 for numeric IDs), cancellation of
 * superseded requests, stale results kept while loading, and a delayed loading bar. Implemented directly
 * (not `useAsyncList`) because the minimum length, the delayed bar and the retry-with-the-same-query
 * behaviour are part of the spec.
 */
export function useAsyncOptions<T>(
  load: LoadOptions<T> | undefined,
  query: string,
  minLength?: number,
): AsyncOptions<T> {
  const trimmed = query.trim();
  const min = minQueryLengthFor(trimmed, minLength);
  const [attempt, setAttempt] = useState(0);
  const [result, setResult] = useState<Result<T>>({ key: '', items: [], failed: false });
  const [barKey, setBarKey] = useState('');
  const enabled = load !== undefined && trimmed.length >= min;
  const requestKey = `${trimmed}\u0000${String(attempt)}`;

  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    const debounce = setTimeout(() => {
      load(trimmed, controller.signal).then(
        (items) => {
          if (!controller.signal.aborted) setResult({ key: requestKey, items, failed: false });
        },
        () => {
          if (!controller.signal.aborted) {
            setResult((previous) => ({ key: requestKey, items: previous.items, failed: true }));
          }
        },
      );
    }, ASYNC_DEBOUNCE_MS);
    const bar = setTimeout(() => {
      setBarKey(requestKey);
    }, durations.delaySkeleton);
    return () => {
      clearTimeout(debounce);
      clearTimeout(bar);
      controller.abort();
    };
  }, [enabled, load, trimmed, requestKey]);

  const retry = useCallback(() => {
    setAttempt((n) => n + 1);
  }, []);

  if (load === undefined) {
    return { status: 'idle', items: [], isStale: false, showBar: false, minLength: min, retry };
  }
  if (!enabled) {
    return { status: 'tooShort', items: [], isStale: false, showBar: false, minLength: min, retry };
  }
  const loading = result.key !== requestKey;
  if (loading) {
    return {
      status: 'loading',
      items: result.items,
      isStale: result.items.length > 0,
      showBar: barKey === requestKey,
      minLength: min,
      retry,
    };
  }
  return {
    status: result.failed ? 'error' : 'ready',
    items: result.items,
    isStale: false,
    showBar: false,
    minLength: min,
    retry,
  };
}
