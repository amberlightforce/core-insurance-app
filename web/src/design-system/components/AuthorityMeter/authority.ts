/** Authority meter thresholds (Part 2 §4.6, §4.24): success within 80 %, warning 80–100 %, danger beyond. */
export type AuthorityStatus = 'success' | 'warning' | 'danger';

export const WARNING_RATIO = 0.8;

export function authorityStatus(value: number, limit: number): AuthorityStatus {
  if (limit <= 0) return value > 0 ? 'danger' : 'success';
  const ratio = Math.abs(value) / limit;
  if (ratio > 1) return 'danger';
  if (ratio >= WARNING_RATIO) return 'warning';
  return 'success';
}

/** Decimal string or number → number, for bar geometry only (never for the displayed figures). */
export function toNumber(value: number | string): number {
  const n = typeof value === 'number' ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
}
