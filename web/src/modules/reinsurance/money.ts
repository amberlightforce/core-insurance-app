/**
 * Exact money and percentage arithmetic on decimal strings (BigInt, no binary floating point). Amounts become minor
 * units (cents); signed lines become millionths of a percent. Only layout proportions ever use `Number`.
 */

/** «1234.5» → 123450n (two decimals; further digits truncate, the contract carries cents only). */
export function toMinor(amount: string): bigint {
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(amount.trim());
  if (!match) return 0n;
  const [, sign, whole, fraction = ''] = match;
  const cents = BigInt(`${whole ?? '0'}${fraction.padEnd(2, '0').slice(0, 2)}`);
  return sign === '-' ? -cents : cents;
}

/** 123450n → «1234.50». */
export function fromMinor(minor: bigint): string {
  const negative = minor < 0n;
  const digits = (negative ? -minor : minor).toString().padStart(3, '0');
  return `${negative ? '-' : ''}${digits.slice(0, -2)}.${digits.slice(-2)}`;
}

/** «37.5» → 37_500_000n (millionths of a percent), so signed lines sum exactly. */
export function pctToMicro(pct: string): bigint {
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(pct.trim());
  if (!match) return 0n;
  const [, sign, whole, fraction = ''] = match;
  const value = BigInt(`${whole ?? '0'}${fraction.padEnd(6, '0').slice(0, 6)}`);
  return sign === '-' ? -value : value;
}

/** 37_500_000n → «37.5» (trailing zeros dropped), for the percent formatter. */
export function microToPct(micro: bigint): string {
  const digits = (micro < 0n ? -micro : micro).toString().padStart(7, '0');
  const whole = digits.slice(0, -6);
  const fraction = digits.slice(-6).replace(/0+$/, '');
  return `${micro < 0n ? '-' : ''}${whole}${fraction ? `.${fraction}` : ''}`;
}

/**
 * Splits `total` (minor units, >= 0) over `weights` by largest remainder: shares are never negative and sum to
 * `total` exactly (D-SL4-22). Ties go to the earlier index. With no weight at all the first share takes everything.
 */
export function allocateLargestRemainder(total: bigint, weights: readonly bigint[]): bigint[] {
  if (weights.length === 0) return [];
  const sum = weights.reduce((acc, weight) => acc + weight, 0n);
  if (sum <= 0n || total <= 0n) return weights.map((_, index) => (index === 0 ? total : 0n));
  const floors = weights.map((weight) => (total * weight) / sum);
  const remainders = weights.map((weight, index) => ({ index, rest: (total * weight) % sum }));
  let left = total - floors.reduce((acc, share) => acc + share, 0n);
  remainders.sort((a, b) => (a.rest === b.rest ? a.index - b.index : a.rest > b.rest ? -1 : 1));
  const shares = [...floors];
  for (const { index } of remainders) {
    if (left <= 0n) break;
    shares[index] = (shares[index] ?? 0n) + 1n;
    left -= 1n;
  }
  return shares;
}

/** A BigInt ratio as a 0..1 number for layout only (never for amounts shown or summed). */
export function ratio(part: bigint, whole: bigint): number {
  if (whole <= 0n) return 0;
  return Number((part * 10_000n) / whole) / 10_000;
}
