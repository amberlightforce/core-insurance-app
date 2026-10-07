/**
 * KPI value composition (Part 3 §6.4). All number, money and percent formatting goes through `src/format`
 * (D-FE-25); this file only decides which form a KPI tile shows.
 */
import {
  formatCompact,
  formatMoney,
  formatNumber,
  formatPercent,
  formatPercentagePoints,
  type RegionFormat,
} from '../../../format/numbers';

/**
 * Abbreviated money for tiles and axes: < 10.000 full without decimals («8.420 €»), 10.000–99.999 one
 * decimal («48,2 χιλ. €»), 100.000–999.999 none («482 χιλ. €»), millions and billions two decimals
 * («1,28 εκ. €»). The full value always goes in the tooltip and accessible name.
 */
export function formatCompactMoney(value: number, region: RegionFormat, currency = 'EUR'): string {
  const abs = Math.abs(value);
  if (abs < 10_000) return formatMoney(value, { region, currency, fractionDigits: 0 });
  const digits = abs < 100_000 ? 1 : abs < 1_000_000 ? 0 : 2;
  return formatCompact(value, { region, maximumFractionDigits: digits, currency });
}

/** Counts are never abbreviated below 10.000; above, compact with one decimal. */
export function formatCompactNumber(value: number, region: RegionFormat): string {
  if (Math.abs(value) < 10_000) return formatNumber(value, { region, maximumFractionDigits: 0 });
  return formatCompact(value, { region, maximumFractionDigits: 1 });
}

export type DeltaUnit = 'percent' | 'points' | 'number';

/** A delta with its sign always shown: «+4,2 %», «−1,8 %», «+2,1 μ.» (percentage points), «+12». */
export function formatDelta(value: number, unit: DeltaUnit, region: RegionFormat): string {
  if (unit === 'percent') {
    return formatPercent(value, { region, fractionDigits: 1, signDisplay: 'exceptZero' });
  }
  if (unit === 'points') return formatPercentagePoints(value, { region, fractionDigits: 1 });
  return formatNumber(value, { region, maximumFractionDigits: 1, signDisplay: 'exceptZero' });
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
        full: formatMoney(value, { region, currency }),
      };
    case 'percent': {
      const text = formatPercent(value, { region, fractionDigits: 1 });
      return { short: text, full: text };
    }
    case 'count':
      return {
        short: formatCompactNumber(value, region),
        full: formatNumber(value, { region, maximumFractionDigits: 0 }),
      };
    case 'number': {
      const digits = Number.isInteger(value) ? 0 : 1;
      const text = formatNumber(value, {
        region,
        minimumFractionDigits: digits,
        maximumFractionDigits: digits,
      });
      return { short: text, full: text };
    }
  }
}
