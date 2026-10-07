/**
 * Live announcer (WCAG 4.1.3): status messages without moving focus. Two visually hidden regions
 * (polite and assertive) are created on first use. React Aria keeps its own announcer private, so the
 * design system owns this one; every component announces through it.
 */
export type Politeness = 'polite' | 'assertive';

const regionIds: Record<Politeness, string> = {
  polite: 'ds-live-polite',
  assertive: 'ds-live-assertive',
};

function region(politeness: Politeness): HTMLElement | null {
  if (typeof document === 'undefined') return null;
  const existing = document.getElementById(regionIds[politeness]);
  if (existing) return existing;
  const el = document.createElement('div');
  el.id = regionIds[politeness];
  el.className = 'ds-visually-hidden';
  el.setAttribute('aria-live', politeness);
  el.setAttribute('aria-atomic', 'true');
  if (politeness === 'assertive') el.setAttribute('role', 'alert');
  else el.setAttribute('role', 'status');
  document.body.appendChild(el);
  return el;
}

const timers = new Map<Politeness, ReturnType<typeof setTimeout>>();

/** Announces `message`. Repeating the same text re-announces it (the region is cleared first). */
export function announce(message: string, politeness: Politeness = 'polite'): void {
  const el = region(politeness);
  if (!el) return;
  el.textContent = '';
  const pending = timers.get(politeness);
  if (pending) clearTimeout(pending);
  timers.set(
    politeness,
    setTimeout(() => {
      el.textContent = message;
    }, 50),
  );
}

/** Clears both regions (used by tests). */
export function clearAnnouncements(): void {
  for (const politeness of ['polite', 'assertive'] as const) {
    const pending = timers.get(politeness);
    if (pending) clearTimeout(pending);
    const el =
      typeof document === 'undefined' ? null : document.getElementById(regionIds[politeness]);
    if (el) el.textContent = '';
  }
}
