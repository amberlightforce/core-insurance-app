import type { Money } from '../../api/types';

const SCALE = 4;

/** Decimal string → integer in 1/10^4 units (money is decimal strings end to end; never floats). */
function toUnits(amount: string): bigint {
  const negative = amount.startsWith('-');
  const [whole = '0', fraction = ''] = amount.replace('-', '').split('.');
  const units =
    BigInt(whole) * 10n ** BigInt(SCALE) + BigInt(fraction.padEnd(SCALE, '0').slice(0, SCALE));
  return negative ? -units : units;
}

function fromUnits(units: bigint): string {
  const negative = units < 0n;
  const abs = negative ? -units : units;
  const base = 10n ** BigInt(SCALE);
  const fraction = (abs % base).toString().padStart(SCALE, '0').slice(0, 2);
  return `${negative ? '-' : ''}${(abs / base).toString()}.${fraction}`;
}

/** Sums amounts per currency exactly. */
export function sumByCurrency(values: Money[]): Record<string, string> {
  const totals = new Map<string, bigint>();
  for (const v of values)
    totals.set(v.currency, (totals.get(v.currency) ?? 0n) + toUnits(v.amount));
  return Object.fromEntries([...totals].map(([currency, units]) => [currency, fromUnits(units)]));
}

export interface StatusRow {
  status: string;
  count: number;
  open: Money[];
}

/** Groups items by a status, counting them and keeping the amounts to sum. */
export function groupByStatus<T>(
  items: T[],
  statusOf: (item: T) => string,
  openOf: (item: T) => Money,
): StatusRow[] {
  const rows = new Map<string, StatusRow>();
  for (const item of items) {
    const status = statusOf(item);
    const row = rows.get(status) ?? { status, count: 0, open: [] };
    row.count += 1;
    row.open.push(openOf(item));
    rows.set(status, row);
  }
  return [...rows.values()];
}
