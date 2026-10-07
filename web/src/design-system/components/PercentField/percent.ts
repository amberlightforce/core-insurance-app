import type { RegionFormat } from '../../../format/numbers';

/**
 * Parses the text of a percent input in a region format («7,5», «−7,5», «1.250,25» / «7.5», «1,250.25»).
 * Returns NaN for anything else. Used to step from what is typed, before React Aria commits it.
 */
export function parsePercentText(text: string, region: RegionFormat): number {
  const group = region === 'en-GB' ? ',' : '.';
  const decimal = region === 'en-GB' ? '.' : ',';
  let s = text.replace(/[\s\u00a0\u202f%‰+]/g, '').replace(/\u2212/g, '-');
  s = s.split(group).join('');
  s = s.replace(decimal, '.');
  if (!/^-?(\d+\.?\d*|\.\d+)$/.test(s)) return Number.NaN;
  return Number(s);
}

/** Adds `delta` and rounds to `fractionDigits` (avoids 0.1 + 0.2 drift), then clamps. */
export function stepValue(
  current: number,
  delta: number,
  fractionDigits: number,
  minValue: number | undefined,
  maxValue: number | undefined,
): number {
  const factor = 10 ** fractionDigits;
  let next = Math.round((current + delta) * factor) / factor;
  if (minValue !== undefined) next = Math.max(minValue, next);
  if (maxValue !== undefined) next = Math.min(maxValue, next);
  return next;
}
