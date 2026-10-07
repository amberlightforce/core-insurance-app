/**
 * Number, money and percentage formatting (Part 4 §8.2). Formats follow the user's REGION FORMAT, never the
 * UI language: pass the region (default `el-GR`), typically from `useRegionFormat()`.
 *
 * - Money «1.234,56 €» (NBSP before €), negative «−1.234,56 €» with U+2212.
 * - Percent «15 %» with U+202F in el-GR, «15%» in en-GB. Per mille «2,5 ‰».
 * - Abbreviations «1,28 χιλ.», «1,28 εκ.», «12,35 δισ.» (en: K, M, B).
 *
 * Values may be numbers or exact decimal strings ("1234.56"); strings are formatted without going through a
 * float, so amounts never lose precision.
 */
export type RegionFormat = 'el-GR' | 'en-GB';
export type Numeric = number | string | bigint;

export const MINUS_SIGN = '−';
export const NBSP = ' ';
export const NARROW_NBSP = ' ';

/** Minor-unit precision by currency (MKT currency rules decide in production; ISO 4217 defaults here). */
const currencyDigits: Record<string, number> = { EUR: 2, USD: 2, GBP: 2, BGN: 2, JPY: 0 };

export function currencyFractionDigits(currency: string): number {
  return currencyDigits[currency] ?? 2;
}

function asIntlValue(value: Numeric): number | bigint | `${number}` {
  if (typeof value === 'string') {
    const trimmed = value.trim();
    if (!/^[+-]?(\d+\.?\d*|\.\d+)(e[+-]?\d+)?$/i.test(trimmed)) {
      throw new RangeError(`Not a decimal number: ${value}`);
    }
    return trimmed as `${number}`;
  }
  return value;
}

/** Replaces the ASCII hyphen-minus that Intl emits with the true minus sign U+2212. */
function withTrueMinus(parts: Intl.NumberFormatPart[]): string {
  return parts.map((p) => (p.type === 'minusSign' ? MINUS_SIGN : p.value)).join('');
}

export interface MoneyOptions {
  currency?: string;
  region?: RegionFormat;
  /** `always` shows «+» on positive values (deltas). */
  signDisplay?: 'auto' | 'always' | 'exceptZero' | 'never';
  /** Show the currency symbol (default) or only the number (table cells with the unit in the header). */
  showCurrency?: boolean;
}

/** «1.234,56 €» / «−1.234,56 €» / en-GB «€1,234.56». Precision comes from the currency. */
export function formatMoney(value: Numeric, options: MoneyOptions = {}): string {
  const { currency = 'EUR', region = 'el-GR', signDisplay = 'auto', showCurrency = true } = options;
  const digits = currencyFractionDigits(currency);
  const formatter = new Intl.NumberFormat(region, {
    ...(showCurrency ? { style: 'currency', currency } : {}),
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
    signDisplay,
  });
  return withTrueMinus(formatter.formatToParts(asIntlValue(value)));
}

export interface NumberOptions {
  region?: RegionFormat;
  minimumFractionDigits?: number;
  maximumFractionDigits?: number;
  signDisplay?: 'auto' | 'always' | 'exceptZero' | 'never';
}

/** «1.234.567,89» / en-GB «1,234,567.89», with U+2212 for negatives. */
export function formatNumber(value: Numeric, options: NumberOptions = {}): string {
  const { region = 'el-GR', minimumFractionDigits = 0, maximumFractionDigits = 2, signDisplay } = options;
  const formatter = new Intl.NumberFormat(region, {
    minimumFractionDigits,
    maximumFractionDigits: Math.max(minimumFractionDigits, maximumFractionDigits),
    ...(signDisplay ? { signDisplay } : {}),
  });
  return withTrueMinus(formatter.formatToParts(asIntlValue(value)));
}

/** Integer with grouping, e.g. counts «4.812». */
export function formatInteger(value: Numeric, region: RegionFormat = 'el-GR'): string {
  return formatNumber(value, { region, maximumFractionDigits: 0 });
}

export interface PercentOptions {
  region?: RegionFormat;
  /** Fixed decimals (2 by default; 4 for rating). */
  fractionDigits?: number;
  signDisplay?: 'auto' | 'always' | 'exceptZero';
}

function unitSuffix(region: RegionFormat, symbol: string): string {
  return region === 'el-GR' ? `${NARROW_NBSP}${symbol}` : symbol;
}

/**
 * Percent from a percentage value (15 → «15 %»), not a ratio. el-GR puts U+202F before % (Part 4 §8.2).
 */
export function formatPercent(percent: Numeric, options: PercentOptions = {}): string {
  const { region = 'el-GR', fractionDigits = 2, signDisplay } = options;
  const number = formatNumber(percent, {
    region,
    minimumFractionDigits: fractionDigits,
    maximumFractionDigits: fractionDigits,
    ...(signDisplay ? { signDisplay } : {}),
  });
  return `${number}${unitSuffix(region, '%')}`;
}

/** Per mille from a per-mille value (2.5 → «2,5 ‰»). */
export function formatPerMille(perMille: Numeric, options: PercentOptions = {}): string {
  const { region = 'el-GR', fractionDigits = 1, signDisplay } = options;
  const number = formatNumber(perMille, {
    region,
    minimumFractionDigits: fractionDigits,
    maximumFractionDigits: fractionDigits,
    ...(signDisplay ? { signDisplay } : {}),
  });
  return `${number}${unitSuffix(region, '‰')}`;
}

/** Percentage points for deltas between percentages: «+1,50 π.μ.» / «+1.50 pp». */
export function formatPercentagePoints(points: Numeric, options: PercentOptions = {}): string {
  const { region = 'el-GR', fractionDigits = 2, signDisplay = 'exceptZero' } = options;
  const number = formatNumber(points, {
    region,
    minimumFractionDigits: fractionDigits,
    maximumFractionDigits: fractionDigits,
    signDisplay,
  });
  return region === 'el-GR' ? `${number}${NBSP}π.μ.` : `${number}${NBSP}pp`;
}

export interface CompactOptions {
  region?: RegionFormat;
  /** Decimals kept in the abbreviation (KPI tiles use 2: «1,28 εκ. €»). */
  maximumFractionDigits?: number;
  /** Append the currency symbol for money: «1,28 εκ. €» / «€1.28M». */
  currency?: string;
}

/**
 * Abbreviated figures for KPI tiles (Part 2 §4.27): «1,23 χιλ.», «1,28 εκ.», «12,35 δισ.»; en-GB «1.28M».
 * Always give the full value in the tooltip and the accessible name.
 */
export function formatCompact(value: number, options: CompactOptions = {}): string {
  const { region = 'el-GR', maximumFractionDigits = 2, currency } = options;
  const formatter = new Intl.NumberFormat(region, { notation: 'compact', maximumFractionDigits });
  const number = withTrueMinus(formatter.formatToParts(value));
  if (!currency) return number;
  const symbol =
    new Intl.NumberFormat(region, { style: 'currency', currency })
      .formatToParts(0)
      .find((p) => p.type === 'currency')?.value ?? currency;
  return region === 'el-GR' ? `${number}${NBSP}${symbol}` : `${symbol}${number}`;
}
