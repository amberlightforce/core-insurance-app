import { useEffect, useRef, useState, type KeyboardEvent } from 'react';

import { isAcceleratorKey } from '../../../format/date-input';

export type QuickEntrySlot = 'start' | 'end' | 'single';

export interface QuickEntryRequest {
  text: string;
  slot: QuickEntrySlot;
}

export interface QuickEntryController {
  request: QuickEntryRequest | null;
  /** Attach to the field wrapper (capture phase, before the segments see the key). */
  onKeyDownCapture: (e: KeyboardEvent<HTMLElement>) => void;
  /** Close the box; `restoreFocus` returns focus to the first segment of the slot. */
  close: (restoreFocus: boolean) => void;
}

/**
 * The key-capture layer of the date accelerators (D-FE-08). Typing a letter, «+» or «-» in a date segment
 * (or pressing Ctrl+Space) opens the quick-entry box with that character. Segments are found by their
 * `spinbutton` role; the slot comes from the nearest `[data-quick-slot]`.
 */
export function useQuickEntry(enabled: boolean): QuickEntryController {
  const [request, setRequest] = useState<QuickEntryRequest | null>(null);
  const [focusRequest, setFocusRequest] = useState<{ slot: QuickEntrySlot; n: number } | null>(
    null,
  );
  const containerRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (!focusRequest) return;
    const root = containerRef.current;
    const segment = root?.querySelector<HTMLElement>(
      `[data-quick-slot="${focusRequest.slot}"] [role="spinbutton"]`,
    );
    segment?.focus();
  }, [focusRequest]);

  const onKeyDownCapture = (e: KeyboardEvent<HTMLElement>) => {
    if (!enabled || request) return;
    const target = e.target as HTMLElement;
    if (target.getAttribute('role') !== 'spinbutton') return;
    const ctrlSpace = (e.ctrlKey || e.metaKey) && (e.key === ' ' || e.code === 'Space');
    if (!ctrlSpace && (e.ctrlKey || e.metaKey || e.altKey || !isAcceleratorKey(e.key))) return;
    e.preventDefault();
    e.stopPropagation();
    containerRef.current = e.currentTarget;
    const slotAttr = target.closest('[data-quick-slot]')?.getAttribute('data-quick-slot');
    const slot: QuickEntrySlot = slotAttr === 'start' || slotAttr === 'end' ? slotAttr : 'single';
    setRequest({ text: ctrlSpace ? '' : e.key, slot });
  };

  const close = (restoreFocus: boolean) => {
    const slot = request?.slot ?? 'single';
    setRequest(null);
    if (restoreFocus) setFocusRequest((previous) => ({ slot, n: (previous?.n ?? 0) + 1 }));
  };

  return { request, onKeyDownCapture, close };
}
