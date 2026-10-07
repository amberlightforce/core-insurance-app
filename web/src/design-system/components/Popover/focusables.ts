const tabbableSelector = [
  'a[href]',
  'area[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]',
  '[contenteditable="true"]',
].join(',');

/** Elements inside `root` reachable with Tab, in DOM order. */
export function tabbablesIn(root: Element): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(tabbableSelector)).filter(
    (el) => el.tabIndex >= 0 && !el.closest('[hidden], [inert]'),
  );
}

const fieldSelector =
  'input:not([type="hidden"]):not([disabled]), textarea:not([disabled]), select:not([disabled]), [role="combobox"], [role="textbox"], button[aria-haspopup="listbox"]:not([disabled])';

/** The first form field inside `root` (initial focus for dialogs and sheets, Part 2 §4.18/§4.20). */
export function firstFieldIn(root: Element): HTMLElement | null {
  const field = Array.from(root.querySelectorAll<HTMLElement>(fieldSelector)).find(
    (el) => el.tabIndex >= 0 && !el.closest('[hidden], [inert]'),
  );
  return field ?? null;
}

/**
 * Non-modal popover rule (Part 2 §4.16): Tab past the last element (or Shift+Tab before the first) closes
 * the popover instead of wrapping. Returns true when it handled the key.
 */
export function isTabPastEdge(event: KeyboardEvent | React.KeyboardEvent, root: Element): boolean {
  if (event.key !== 'Tab' || event.altKey || event.ctrlKey || event.metaKey) return false;
  const tabbables = tabbablesIn(root);
  const active = document.activeElement;
  if (tabbables.length === 0) return true;
  if (!event.shiftKey) return active === tabbables[tabbables.length - 1];
  return active === tabbables[0] || active === root;
}
