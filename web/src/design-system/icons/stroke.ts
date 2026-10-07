export type IconSize = 12 | 14 | 16 | 20 | 24 | 32 | 40;

/** Stroke by size (Part 3 §7.1): 1.5 px at 12–16, 1.75 at 20, 2 at ≥24, non-scaling. */
export function strokeForSize(size: IconSize): number {
  if (size <= 16) return 1.5;
  if (size === 20) return 1.75;
  return 2;
}
