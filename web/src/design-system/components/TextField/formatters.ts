/** Display formatter for TextField: the raw (stored, emitted) value and what the input shows. */
export interface FieldFormatter {
  /** Display text → raw value. Must be prefix-stable: `parse(prefix)` is a prefix of `parse(full)`. */
  parse: (display: string) => string;
  /** Raw value → display text. */
  format: (raw: string) => string;
  /** Re-format while typing, keeping the caret on the same significant character; otherwise on blur. */
  live?: boolean;
}

/**
 * Index in `display` just after the `significant`-th significant character (as counted by `parse`), so that
 * the caret stays put when grouping spaces are inserted or removed while typing.
 */
export function caretIndexFor(
  display: string,
  significant: number,
  parse: (text: string) => string,
): number {
  if (significant <= 0) return 0;
  for (let i = 1; i <= display.length; i++) {
    if (parse(display.slice(0, i)).length >= significant) return i;
  }
  return display.length;
}

/** Greek national number: digits only; a typed `+30`/`0030` country code is dropped. */
export function parsePhone(display: string): string {
  let digits = display.replace(/\D/g, '');
  if (digits.startsWith('0030')) digits = digits.slice(4);
  else if (digits.length > 10 && digits.startsWith('30')) digits = digits.slice(2);
  return digits.slice(0, 10);
}

/** «6941234567» → «694 123 4567», «2101234567» → «210 123 4567»; other lengths are left ungrouped. */
export function formatPhone(raw: string): string {
  if (raw.length !== 10) return raw;
  return `${raw.slice(0, 3)} ${raw.slice(3, 6)} ${raw.slice(6)}`;
}

/** `tel` variant: groups «69x xxx xxxx» on blur (Part 2 §4.2). */
export const phoneFormatter: FieldFormatter = {
  parse: parsePhone,
  format: formatPhone,
  live: false,
};
