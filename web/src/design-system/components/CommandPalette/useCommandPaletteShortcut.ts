import { useEffect, useRef } from 'react';

import { matchesShortcut } from '../Kbd';

/**
 * Ctrl/⌘+K toggles the command palette from anywhere, including inside text inputs (Part 2 §4.36). The
 * listener runs in the capture phase so fields that stop propagation cannot swallow it.
 */
export function useCommandPaletteShortcut(onToggle: () => void, isEnabled = true): void {
  const latest = useRef(onToggle);
  useEffect(() => {
    latest.current = onToggle;
  });

  useEffect(() => {
    if (!isEnabled) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.repeat || !matchesShortcut(event, 'Mod+K')) return;
      event.preventDefault();
      latest.current();
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => {
      window.removeEventListener('keydown', onKeyDown, true);
    };
  }, [isEnabled]);
}
