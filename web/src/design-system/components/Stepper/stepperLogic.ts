/** Node states (Part 2 §4.23). The current step is given separately (`currentId`). */
export type StepState = 'upcoming' | 'complete' | 'error' | 'warning' | 'locked' | 'loading';

export interface StepItem {
  id: string;
  /** Consumer content, e.g. «Οχήματα». */
  label: string;
  state: StepState;
  /** Caption, e.g. «3 οχήματα · 2 οδηγοί» or «2 σφάλματα». */
  subLabel?: string;
  /** Shown in the error node next to the alert icon. */
  errorCount?: number;
  /** Why a locked step cannot be opened. */
  lockedReason?: string;
  /** Render the step as a link (wizard steps with their own URL). */
  href?: string;
}

export type CanNavigateTo = (step: StepItem, index: number, steps: readonly StepItem[]) => boolean;

const valid = (step: StepItem) => step.state === 'complete' || step.state === 'warning';

/**
 * Default navigation rule: completed, warning and error steps are clickable; an upcoming step only when
 * every step before it is valid (non-linear for experts); locked and loading steps never.
 */
export const defaultCanNavigateTo: CanNavigateTo = (step, index, steps) => {
  switch (step.state) {
    case 'complete':
    case 'warning':
    case 'error':
      return true;
    case 'upcoming':
      return steps.slice(0, index).every(valid);
    case 'locked':
    case 'loading':
      return false;
  }
};

/** The nearest navigable step before or after `currentId` (Alt+←/→). */
export function adjacentNavigableStep(
  steps: readonly StepItem[],
  currentId: string,
  direction: 1 | -1,
  canNavigateTo: CanNavigateTo = defaultCanNavigateTo,
): StepItem | undefined {
  const index = steps.findIndex((step) => step.id === currentId);
  for (let i = index + direction; i >= 0 && i < steps.length; i += direction) {
    const step = steps[i];
    if (step && canNavigateTo(step, i, steps)) return step;
  }
  return undefined;
}

/** Horizontal layout shows the current step ±1. */
export function horizontalWindow(steps: readonly StepItem[], currentIndex: number): number[] {
  const start = Math.max(0, Math.min(currentIndex - 1, steps.length - 3));
  return Array.from({ length: Math.min(3, steps.length) }, (_, i) => start + i);
}
