/**
 * Display formatters for KPI tiles, statements and file sizes (Part 3 §6.4, Part 4 §8.2). They follow the
 * region-format preference (never the UI language): el-GR «1.284.390,12 €», «−1,8 %» (U+2212 minus,
 * NBSP before €, narrow NBSP before %), en-GB «€1,284,390.12», «−1.8%».
 */
import type { RegionFormat } from '../../preferences';

export const MINUS = '\u2212';
const NARROW_NBSP = '\u202F';

function join(parts: Intl.NumberFormatPart[]): string {
  return parts.map((p) => (p.type === 'minusSign' ? MINUS : p.value)).join('');
}

/** Full money value with the currency's precision: «1.284.390,12 €», «−1.234,50 €». */
export function formatMoney(
  value: number,
  region: RegionFormat,
  currency = 'EUR',
  options: { fractionDigits?: number; signDisplay?: 'auto' | 'always' | 'exceptZero' } = {},
): string {
  const { fractionDigits = 2, signDisplay = 'auto' } = options;
  return join(
    new Intl.NumberFormat(region, {
      style: 'currency',
      currency,
      minimumFractionDigits: fractionDigits,
      maximumFractionDigits: fractionDigits,
      useGrouping: 'always',
      signDisplay,
    }).formatToParts(value),
  );
}

/**
 * Abbreviated money for tiles and axes (Part 3 §6.4): < 10.000 full without decimals («8.420 €»),
 * 10.000–99.999 one decimal («48,2 χιλ. €»), 100.000–999.999 none («482 χιλ. €»), millions and billions
 * two decimals («1,28 εκ. €», «1,28 δισ. €»). The full value always goes in the tooltip and accessible name.
 */
export function formatCompactMoney(value: number, region: RegionFormat, currency = 'EUR'): string {
  const abs = Math.abs(value);
  if (abs < 10_000) return formatMoney(value, region, currency, { fractionDigits: 0 });
  const digits = abs < 100_000 ? 1 : abs < 1_000_000 ? 0 : 2;
  return join(
    new Intl.NumberFormat(region, {
      style: 'currency',
      currency,
      notation: 'compact',
      maximumFractionDigits: digits,
    }).formatToParts(value),
  );
}

/** Integers and decimals with grouping always on («4.812»; el-GR CLDR would otherwise skip 4-digit groups). */
export function formatNumber(value: number, region: RegionFormat, fractionDigits = 0): string {
  return join(
    new Intl.NumberFormat(region, {
      minimumFractionDigits: fractionDigits,
      maximumFractionDigits: fractionDigits,
      useGrouping: 'always',
    }).formatToParts(value),
  );
}

/** Counts are never abbreviated below 10.000; above, compact with one decimal. */
export function formatCompactNumber(value: number, region: RegionFormat): string {
  if (Math.abs(value) < 10_000) return formatNumber(value, region);
  return join(
    new Intl.NumberFormat(region, { notation: 'compact', maximumFractionDigits: 1 }).formatToParts(
      value,
    ),
  );
}

/** A percentage given in percent units (68.4 → «68,4 %»). */
export function formatPercent(
  value: number,
  region: RegionFormat,
  options: { fractionDigits?: number; signed?: boolean } = {},
): string {
  const { fractionDigits = 1, signed = false } = options;
  const number = join(
    new Intl.NumberFormat(region, {
      minimumFractionDigits: fractionDigits,
      maximumFractionDigits: fractionDigits,
      signDisplay: signed ? 'exceptZero' : 'auto',
    }).formatToParts(value),
  );
  return region === 'el-GR' ? `${number}${NARROW_NBSP}%` : `${number}%`;
}

export type DeltaUnit = 'percent' | 'points' | 'number';

/** A delta with its sign always shown: «+4,2 %», «−1,8 %», «+2,1 μ.» (percentage points), «+12». */
export function formatDelta(value: number, unit: DeltaUnit, region: RegionFormat): string {
  if (unit === 'percent') return formatPercent(value, region, { signed: true });
  const number = join(
    new Intl.NumberFormat(region, {
      minimumFractionDigits: unit === 'points' ? 1 : 0,
      maximumFractionDigits: 1,
      signDisplay: 'exceptZero',
      useGrouping: 'always',
    }).formatToParts(value),
  );
  if (unit === 'points') return region === 'el-GR' ? `${number}\u00A0μ.` : `${number}\u00A0pp`;
  return number;
}

/** File sizes: «2,4 MB», «820 KB». */
export function formatBytes(bytes: number, region: RegionFormat): string {
  const units = ['byte', 'kilobyte', 'megabyte', 'gigabyte'] as const;
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index += 1;
  }
  return new Intl.NumberFormat(region, {
    style: 'unit',
    unit: units[index],
    unitDisplay: 'short',
    maximumFractionDigits: index >= 2 ? 1 : 0,
  }).format(value);
}

/** 24-hour time «14:32». */
export function formatTime(date: Date, region: RegionFormat): string {
  return new Intl.DateTimeFormat(region, {
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).format(date);
}

/** Short date «07/10/2026». */
export function formatDate(date: Date, region: RegionFormat): string {
  return new Intl.DateTimeFormat(region, {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  }).format(date);
}

export type KpiFormat = 'money' | 'count' | 'percent' | 'number';

/** Abbreviated (tile) and full (tooltip, accessible name) forms of a KPI value. */
export function formatKpiValue(
  value: number,
  format: KpiFormat,
  region: RegionFormat,
  currency = 'EUR',
): { short: string; full: string } {
  switch (format) {
    case 'money':
      return {
        short: formatCompactMoney(value, region, currency),
        full: formatMoney(value, region, currency),
      };
    case 'percent': {
      const text = formatPercent(value, region);
      return { short: text, full: text };
    }
    case 'count':
      return { short: formatCompactNumber(value, region), full: formatNumber(value, region) };
    case 'number': {
      const text = formatNumber(value, region, Number.isInteger(value) ? 0 : 1);
      return { short: text, full: text };
    }
  }
}
