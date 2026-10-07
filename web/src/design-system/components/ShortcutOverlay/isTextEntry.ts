/** True when keystrokes go to text entry, so single-key shortcuts must stay inactive (WCAG 2.1.4). */
export function isTextEntry(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  if (target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) return true;
  if (target instanceof HTMLInputElement) {
    return !['button', 'checkbox', 'radio', 'submit', 'reset', 'range', 'color', 'file'].includes(
      target.type,
    );
  }
  const role = target.getAttribute('role');
  return role === 'textbox' || role === 'combobox' || role === 'searchbox' || role === 'spinbutton';
}
