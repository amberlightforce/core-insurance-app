/**
 * Money INPUT parsing and live formatting for CurrencyField (Part 2 §4.6, DESIGN-A §7.6). Display formatting
 * lives in `numbers.ts`; this module turns what people type or paste into an exact decimal string.
 *
 * - No floating point anywhere: amounts stay decimal strings and integer minor units (bigint).
 * - Extra decimals are REJECTED, never rounded.
 * - el-GR: «,» is the decimal separator and «.» groups thousands; while typing, the numpad «.» also types the
 *   decimal separator. en-GB is the mirror image.
 */
import { formatMoney, MINUS_SIGN, type RegionFormat } from './numbers';

export type MoneyLocale = RegionFormat;

export interface MoneyInputOptions {
  locale?: MoneyLocale;
  /** Precision from the currency rule (2 for EUR). */
  maxFractionDigits?: number;
  allowNegative?: boolean;
  /** Guard against absurd values (default 13 integer digits, i.e. below 10 trillion). */
  maxIntegerDigits?: number;
}

export type MoneyParseError = 'invalid' | 'tooManyDecimals' | 'negativeNotAllowed';

export type MoneyParseResult =
  | {
      ok: true;
      /** Exact decimal string with `maxFractionDigits` decimals and an ASCII «-», e.g. "-1234.50". */
      value: string;
      /** A single «.» (el-GR) or «,» (en-GB) was read as the decimal separator (show «Ερμηνεύτηκε ως …»). */
      interpreted?: true;
    }
  | { ok: false; error: MoneyParseError };

interface Separators {
  decimal: string;
  group: string;
}

function separatorsFor(locale: MoneyLocale): Separators {
  return locale === 'en-GB' ? { decimal: '.', group: ',' } : { decimal: ',', group: '.' };
}

const MINUS = /[-\u2212\u2012\u2013]/;
const SPACES = /[\s\u00a0\u202f\u2009]/g;

/** Shorthand suffixes: thousand (k, χ, χιλ) and million (m, mn, ε, εκ). */
const SHORTHAND: readonly (readonly [RegExp, number])[] = [
  [/^(?:k|χ|χιλ)\.?$/iu, 3],
  [/^(?:m|mn|ε|εκ)\.?$/iu, 6],
];

function escapeRegExp(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

interface DecimalParts {
  int: string;
  frac: string;
  interpreted: boolean;
}

/** Applies the paste rules in order; returns null when the text is not a number. */
function splitDecimal(body: string, sep: Separators): DecimalParts | null {
  if (/^\d+$/.test(body)) return { int: body, frac: '', interpreted: false };
  const grp = escapeRegExp(sep.group);
  const grouped = new RegExp(`^[1-9]\\d{0,2}(?:${grp}\\d{3})+$`);
  const decimalCount = body.split(sep.decimal).length - 1;

  if (decimalCount === 1) {
    // (1) The decimal separator: the integer part is plain digits or correctly grouped.
    const [intPart = '', frac = ''] = body.split(sep.decimal);
    if (!/^\d*$/.test(frac) || (intPart === '' && frac === '')) return null;
    const intDigits = intPart === '' ? '0' : intPart;
    if (/^\d+$/.test(intDigits)) return { int: intDigits, frac, interpreted: false };
    if (grouped.test(intDigits))
      return { int: intDigits.split(sep.group).join(''), frac, interpreted: false };
    return null;
  }
  if (decimalCount > 1) return null;

  // No decimal separator. (2) group separators followed by exactly 3 digits are grouping.
  if (grouped.test(body))
    return { int: body.split(sep.group).join(''), frac: '', interpreted: false };
  // (3) A single group separator with 1–2 trailing digits is read as the decimal separator.
  const single = new RegExp(`^(\\d+)${grp}(\\d{1,2})$`).exec(body);
  if (single) return { int: single[1] ?? '0', frac: single[2] ?? '', interpreted: true };
  // (4) Anything else is invalid.
  return null;
}

function stripLeadingZeros(int: string): string {
  const stripped = int.replace(/^0+/, '');
  return stripped === '' ? '0' : stripped;
}

/** Multiplies a decimal (int, frac) by 10^power without floating point. */
function shiftDecimal(int: string, frac: string, power: number): { int: string; frac: string } {
  const padded = frac.padEnd(power, '0');
  return { int: int + padded.slice(0, power), frac: padded.slice(power) };
}

/**
 * Parses typed or pasted money (DESIGN-A §7.6). Paste rules, in order: (1) the decimal separator with its
 * digits is a decimal; (2) group separators followed by exactly 3 digits are grouping; (3) a single group
 * separator followed by 1–2 digits is read as the decimal separator, flagged `interpreted`; (4) anything else
 * is invalid. Shorthand: `12k`/`12χ` → 12000, `1.5m`/`1,5ε`/`1,5εκ` → 1500000. Signs: «-» or «−» (U+2212),
 * leading or trailing. Currency symbols and spaces are ignored.
 */
export function parseMoneyInput(text: string, options: MoneyInputOptions = {}): MoneyParseResult {
  const {
    locale = 'el-GR',
    maxFractionDigits = 2,
    allowNegative = false,
    maxIntegerDigits = 13,
  } = options;
  let s = text.replace(SPACES, '').replace(/€|eur(?:o|os)?|ευρώ|ευρω/giu, '');
  let negative = false;
  if (s.startsWith('+')) s = s.slice(1);
  if (MINUS.test(s.charAt(0))) {
    negative = true;
    s = s.slice(1);
  } else if (MINUS.test(s.charAt(s.length - 1))) {
    negative = true;
    s = s.slice(0, -1);
  }
  if (s === '') return { ok: false, error: 'invalid' };

  const sep = separatorsFor(locale);
  let parts: DecimalParts | null;

  const suffix = /^([\d.,]+)(\p{L}+\.?)$/u.exec(s);
  if (suffix) {
    const body = suffix[1] ?? '';
    const unit = suffix[2] ?? '';
    const power = SHORTHAND.find(([re]) => re.test(unit))?.[1];
    if (power === undefined) return { ok: false, error: 'invalid' };
    // In shorthand either separator is the decimal separator («1.5m» and «1,5ε»).
    const m = /^(\d+)(?:[.,](\d+))?$/.exec(body);
    if (!m) return { ok: false, error: 'invalid' };
    const shifted = shiftDecimal(m[1] ?? '0', m[2] ?? '', power);
    parts = { int: shifted.int, frac: shifted.frac.replace(/0+$/, ''), interpreted: false };
  } else {
    parts = splitDecimal(s, sep);
  }
  if (!parts) return { ok: false, error: 'invalid' };

  const int = stripLeadingZeros(parts.int);
  if (int.length > maxIntegerDigits) return { ok: false, error: 'invalid' };
  if (parts.frac.length > maxFractionDigits) return { ok: false, error: 'tooManyDecimals' };
  const frac = parts.frac.padEnd(maxFractionDigits, '0');
  const isZero = /^0+$/.test(int + frac);
  if (negative && !isZero && !allowNegative) return { ok: false, error: 'negativeNotAllowed' };
  const value = `${negative && !isZero ? '-' : ''}${int}${maxFractionDigits > 0 ? `.${frac}` : ''}`;
  return parts.interpreted ? { ok: true, value, interpreted: true } : { ok: true, value };
}

/** Number of fraction digits the user has typed after the decimal separator (for live «too many» errors). */
export function typedFractionDigits(text: string, locale: MoneyLocale = 'el-GR'): number {
  const { decimal } = separatorsFor(locale);
  const at = text.indexOf(decimal);
  if (at < 0) return 0;
  return (text.slice(at + 1).match(/\d/g) ?? []).length;
}

export interface LiveFormatResult {
  text: string;
  caret: number;
}

function groupDigits(int: string, group: string): string {
  let out = '';
  for (let i = 0; i < int.length; i++) {
    if (i > 0 && (int.length - i) % 3 === 0) out += group;
    out += int.charAt(i);
  }
  return out;
}

/**
 * Live grouping as you type, keeping the caret on the same significant character. `raw` is the input's
 * text after the keystroke and `caret` its selection start. Keeps a sign, the digits, one decimal separator
 * and a trailing shorthand suffix; group separators are recomputed. In el-GR a «.» just typed (numpad) when
 * there is no «,» yet becomes the decimal separator. Shown with U+2212 for a minus.
 */
export function formatLive(
  raw: string,
  caret: number,
  options: { locale?: MoneyLocale } = {},
): LiveFormatResult {
  const { locale = 'el-GR' } = options;
  const sep = separatorsFor(locale);
  // The numpad «.» (el-GR) or «,» (en-GB) just typed becomes the decimal separator.
  const aliasAt =
    caret > 0 && raw.charAt(caret - 1) === sep.group && !raw.includes(sep.decimal) ? caret - 1 : -1;

  let sign = '';
  let int = '';
  let frac = '';
  let hasDecimal = false;
  let suffix = '';
  let significantBeforeCaret = 0;

  const chars = Array.from(raw);
  let offset = 0;
  for (const ch of chars) {
    const index = offset;
    const before = index < caret;
    offset += ch.length;
    let kept = false;
    if (MINUS.test(ch) && sign === '' && int === '' && !hasDecimal) {
      sign = MINUS_SIGN;
      kept = true;
    } else if (/\d/.test(ch) && suffix === '') {
      if (hasDecimal) frac += ch;
      else int += ch;
      kept = true;
    } else if ((ch === sep.decimal || index === aliasAt) && !hasDecimal && suffix === '') {
      hasDecimal = true;
      kept = true;
    } else if (/[kmnχιλεκ]/iu.test(ch) && (int !== '' || frac !== '') && suffix.length < 3) {
      suffix += ch;
      kept = true;
    }
    if (kept && before) significantBeforeCaret += 1;
  }

  const groupedInt = groupDigits(int, sep.group);
  const text = `${sign}${groupedInt}${hasDecimal ? sep.decimal : ''}${frac}${suffix}`;

  // Place the caret after the same number of significant characters.
  let seen = 0;
  let pos = 0;
  if (significantBeforeCaret > 0) {
    for (const ch of Array.from(text)) {
      pos += ch.length;
      if (ch !== sep.group) seen += 1;
      if (seen === significantBeforeCaret) break;
    }
  }
  return { text, caret: pos };
}

/** Live grouping without a caret: «1234567,8» → «1.234.567,8». */
export function formatMoneyGrouping(text: string, locale: MoneyLocale = 'el-GR'): string {
  return formatLive(text, 0, { locale }).text;
}

/** Display with the currency: «1.234,56 €», «−1.234,56 €» (U+2212, NBSP before €), en-GB «€1,234.56». */
export function formatMoneyDisplay(value: string, locale: MoneyLocale = 'el-GR'): string {
  return formatMoney(value, { region: locale });
}

/** The committed value shown inside the input (no currency; the field shows «€» as a suffix). */
export function formatMoneyInputText(value: string, locale: MoneyLocale = 'el-GR'): string {
  return formatMoney(value, { region: locale, showCurrency: false });
}

/** A decimal string as integer minor units: "1234.5" → 123450n (2 digits). Throws on non-decimals. */
export function toMinor(value: string, fractionDigits = 2): bigint {
  const m = /^([-+]?)(\d+)(?:\.(\d*))?$/.exec(value.trim());
  if (!m) throw new RangeError(`Not a decimal number: ${value}`);
  const frac = (m[3] ?? '').padEnd(fractionDigits, '0');
  if (frac.length > fractionDigits && /[1-9]/.test(frac.slice(fractionDigits))) {
    throw new RangeError(`More than ${String(fractionDigits)} decimals: ${value}`);
  }
  const minor = BigInt((m[2] ?? '0') + frac.slice(0, fractionDigits));
  return m[1] === '-' ? -minor : minor;
}

/** Integer minor units back to a decimal string: 123450n → "1234.50". */
export function fromMinor(minor: bigint, fractionDigits = 2): string {
  const negative = minor < 0n;
  const digits = (negative ? -minor : minor).toString().padStart(fractionDigits + 1, '0');
  const int = digits.slice(0, digits.length - fractionDigits);
  const frac = digits.slice(digits.length - fractionDigits);
  return `${negative ? '-' : ''}${int}${fractionDigits > 0 ? `.${frac}` : ''}`;
}

/** Compares two decimal strings exactly: −1, 0 or 1. */
export function compareMoney(a: string, b: string, fractionDigits = 2): -1 | 0 | 1 {
  const x = toMinor(a, fractionDigits);
  const y = toMinor(b, fractionDigits);
  return x < y ? -1 : x > y ? 1 : 0;
}

/**
 * `amount × percent / 100`, rounded half away from zero to minor units (exact; for deltas such as
 * «−7,50 % = −31,20 €»). `percent` is a decimal string or a number with at most 6 decimals.
 */
export function percentOfAmount(
  amount: string,
  percent: string | number,
  fractionDigits = 2,
): string {
  const SCALE = 6;
  const pctText = typeof percent === 'number' ? percent.toFixed(SCALE) : percent;
  const pct = toMinor(pctText, SCALE);
  const base = toMinor(amount, fractionDigits);
  const denominator = 100n * 10n ** BigInt(SCALE);
  const product = base * pct;
  const negative = product < 0n;
  const abs = negative ? -product : product;
  let quotient = abs / denominator;
  if ((abs % denominator) * 2n >= denominator) quotient += 1n;
  return fromMinor(negative ? -quotient : quotient, fractionDigits);
}
