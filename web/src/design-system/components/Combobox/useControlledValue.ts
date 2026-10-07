import { useCallback, useState } from 'react';

/**
 * Controlled-or-uncontrolled value: `value !== undefined` means controlled (so `null` is a controlled empty
 * value). The setter always calls `onChange`.
 */
export function useControlledValue<T>(
  value: T | undefined,
  defaultValue: T,
  onChange?: (next: T) => void,
): [T, (next: T) => void] {
  const [inner, setInner] = useState<T>(defaultValue);
  const controlled = value !== undefined;
  const current = controlled ? value : inner;
  const set = useCallback(
    (next: T) => {
      if (!controlled) setInner(next);
      onChange?.(next);
    },
    [controlled, onChange],
  );
  return [current, set];
}
