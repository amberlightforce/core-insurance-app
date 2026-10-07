import { toGreekUpper } from '../../../format/greek';

/** Number of avatar hues (`--color-avatar-1` … `--color-avatar-8`, Part 2 §4.25). */
export const avatarHueCount = 8;

/** Deterministic hue 1–8 for a name (djb2 hash), so a person keeps the same colour everywhere. */
export function avatarHue(name: string): number {
  let hash = 5381;
  for (const char of name.normalize('NFC')) {
    hash = ((hash << 5) + hash + (char.codePointAt(0) ?? 0)) | 0;
  }
  return (Math.abs(hash) % avatarHueCount) + 1;
}

/**
 * Two initials: first letter of the first and last word, upper-cased without tonos
 * («Μαρία Παπαδοπούλου» → «ΜΠ», «Ελένη» → «Ε»).
 */
export function initialsOf(name: string): string {
  const words = name
    .trim()
    .split(/[\s.\-–]+/u)
    .filter((word) => /\p{L}/u.test(word));
  const first = words[0]?.match(/\p{L}/u)?.[0] ?? '';
  const last = words.length > 1 ? (words[words.length - 1]?.match(/\p{L}/u)?.[0] ?? '') : '';
  return toGreekUpper(first + last);
}
