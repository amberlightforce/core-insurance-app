import { useEffect, useState } from 'react';

import { useReducedMotion } from '../../preferences/context';

/**
 * Keeps a closing panel mounted for its exit animation. Returns `mounted` (render it) and `exiting`
 * (set `data-exiting`). Under reduced motion the panel unmounts at once.
 */
export function usePresence(
  isOpen: boolean,
  exitMs: number,
): { mounted: boolean; exiting: boolean } {
  const reducedMotion = useReducedMotion();
  const [wasOpen, setWasOpen] = useState(isOpen);
  const [exiting, setExiting] = useState(false);

  // Adjust state while rendering when `isOpen` flips (React's documented pattern, no extra effect pass).
  if (isOpen !== wasOpen) {
    setWasOpen(isOpen);
    setExiting(!isOpen && !reducedMotion);
  }

  useEffect(() => {
    if (!exiting) return;
    const id = setTimeout(() => {
      setExiting(false);
    }, exitMs);
    return () => {
      clearTimeout(id);
    };
  }, [exiting, exitMs]);

  return { mounted: isOpen || exiting, exiting: !isOpen && exiting };
}
