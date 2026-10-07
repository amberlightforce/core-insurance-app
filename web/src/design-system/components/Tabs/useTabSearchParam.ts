import { useCallback } from 'react';
import { useSearchParams } from 'react-router';

/**
 * Deep link for tabs (`?tab=coverages`, Part 2 §4.11). Returns the current value (or `fallback`) and a
 * setter that replaces the history entry, so switching tabs does not flood the back button.
 *
 * `const [tab, setTab] = useTabSearchParam('tab', 'summary');`
 * `<Tabs selectedKey={tab} onSelectionChange={setTab} …/>`
 */
export function useTabSearchParam(
  param = 'tab',
  fallback?: string,
): [string | undefined, (id: string) => void] {
  const [searchParams, setSearchParams] = useSearchParams();
  const value = searchParams.get(param) ?? fallback;
  const setValue = useCallback(
    (id: string) => {
      setSearchParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          next.set(param, id);
          return next;
        },
        { replace: true },
      );
    },
    [param, setSearchParams],
  );
  return [value, setValue];
}
