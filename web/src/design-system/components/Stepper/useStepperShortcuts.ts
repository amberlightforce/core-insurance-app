import { useEffect, useRef } from 'react';

import {
  adjacentNavigableStep,
  defaultCanNavigateTo,
  type CanNavigateTo,
  type StepItem,
} from './stepperLogic';

export interface StepperShortcutOptions {
  steps: readonly StepItem[];
  currentId: string;
  onNavigate: (id: string) => void;
  canNavigateTo?: CanNavigateTo;
  /** Turn off while a modal is open or the wizard is busy. */
  isEnabled?: boolean;
}

/**
 * Alt+→ / Alt+← move to the next / previous navigable wizard step from anywhere on the page
 * (Part 2 §4.23 custom global shortcut). Alt-chords are not single-key shortcuts, so they stay on when
 * the single-key preference is off.
 */
export function useStepperShortcuts({
  steps,
  currentId,
  onNavigate,
  canNavigateTo = defaultCanNavigateTo,
  isEnabled = true,
}: StepperShortcutOptions): void {
  const latest = useRef({ steps, currentId, onNavigate, canNavigateTo });
  useEffect(() => {
    latest.current = { steps, currentId, onNavigate, canNavigateTo };
  });

  useEffect(() => {
    if (!isEnabled) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (!event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
      if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return;
      const {
        steps: list,
        currentId: current,
        onNavigate: navigate,
        canNavigateTo: can,
      } = latest.current;
      const target = adjacentNavigableStep(list, current, event.key === 'ArrowRight' ? 1 : -1, can);
      if (!target) return;
      event.preventDefault();
      navigate(target.id);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [isEnabled]);
}
