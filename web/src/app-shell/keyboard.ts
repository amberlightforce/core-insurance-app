/**
 * Shell keyboard layer (Part 1 §3.4, DESIGN-A §10 item 9): F6 / Shift+F6 cycle the landmark regions;
 * single-key shortcuts (`?`, `[`) are ignored inside text inputs and when the user switched them off
 * (WCAG 2.1.4).
 */
export const regionSelector = '[data-shell-region]';

/** True when the key event comes from a field where typing must not trigger single-key shortcuts. */
export function isTypingTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  const tag = target.tagName;
  if (tag === 'TEXTAREA' || tag === 'SELECT') return true;
  if (tag === 'INPUT') {
    const type = (target as HTMLInputElement).type;
    return !['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color', 'file'].includes(
      type,
    );
  }
  return target.getAttribute('role') === 'combobox' || target.getAttribute('role') === 'textbox';
}

/** Moves focus to the next (or previous) visible landmark region in document order. */
export function cycleRegion(root: ParentNode, backwards: boolean): HTMLElement | null {
  const regions = Array.from(root.querySelectorAll<HTMLElement>(regionSelector)).filter(
    (el) => !el.hidden && el.getAttribute('aria-hidden') !== 'true',
  );
  if (regions.length === 0) return null;
  const active = document.activeElement;
  const currentIndex = regions.findIndex((region) => region === active || region.contains(active));
  const step = backwards ? -1 : 1;
  const nextIndex =
    currentIndex === -1
      ? backwards
        ? regions.length - 1
        : 0
      : (currentIndex + step + regions.length) % regions.length;
  const next = regions[nextIndex];
  if (!next) return null;
  if (!next.hasAttribute('tabindex')) next.setAttribute('tabindex', '-1');
  next.focus();
  return next;
}
