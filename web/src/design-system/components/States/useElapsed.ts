import { useEffect, useState } from 'react';

/**
 * True once `ms` milliseconds have passed since mount (or since `active` became true). Used for the 150 ms
 * skeleton delay (no flash on fast loads) and the 2 s switch to the long-loading state.
 */
export function useElapsed(ms: number, active = true): boolean {
  const [elapsed, setElapsed] = useState(ms <= 0);
  useEffect(() => {
    if (!active || ms <= 0) return;
    const id = setTimeout(() => {
      setElapsed(true);
    }, ms);
    return () => {
      clearTimeout(id);
    };
  }, [ms, active]);
  return ms <= 0 || (active && elapsed);
}
