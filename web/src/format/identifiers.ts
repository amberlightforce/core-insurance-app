/**
 * Identifier normalisers, formatters and validators (Part 2 §4.7). Pure functions, no UI.
 *
 * Every identifier has three forms:
 * - **raw**: what is stored and emitted (`normalize*`): no grouping, upper case, no separators;
 * - **display**: what the field shows (`format*`): grouped for reading;
 * - **validation**: `validate*` returns `null` or the first issue, with a severity (a VIN check digit is only a
 *   warning) and parameters for the message.
 *
 * `normalize*(prefix)` is always a prefix of `normalize*(full)`, so a caller can keep the caret on the same
 * significant character after re-formatting.
 */

export type IdentifierKind = 'afm' | 'iban' | 'plate' | 'vin' | 'reference';

export interface IdentifierIssue {
  /** Stable code, used as the message key (`identifierField.errors.<kind>.<code>`). */
  code: string;
  severity: 'error' | 'warning';
  params?: Record<string, string | number>;
}

function error(code: string, params?: Record<string, string | number>): IdentifierIssue {
  return params ? { code, severity: 'error', params } : { code, severity: 'error' };
}

/* ΑΦΜ (Greek tax number) ------------------------------------------------------------------------ */

export const AFM_LENGTH = 9;

/** Digits only, at most 9. */
export function normalizeAfm(input: string): string {
  return input.replace(/\D/g, '').slice(0, AFM_LENGTH);
}

/** «090000045» → «090 000 045». */
export function formatAfm(raw: string): string {
  return groupEvery(raw, 3);
}

/**
 * ΑΦΜ check (mod 11): the first 8 digits weighted 2^8 … 2^1, the sum mod 11 mod 10 must equal the 9th digit.
 * All zeros is rejected.
 */
export function isValidAfmChecksum(raw: string): boolean {
  if (!/^\d{9}$/.test(raw) || raw === '000000000') return false;
  let sum = 0;
  for (let i = 0; i < 8; i++) {
    sum += Number(raw[i]) * 2 ** (8 - i);
  }
  return (sum % 11) % 10 === Number(raw[8]);
}

export function validateAfm(raw: string): IdentifierIssue | null {
  if (raw.length !== AFM_LENGTH) return error('length', { length: AFM_LENGTH });
  if (!isValidAfmChecksum(raw)) return error('checksum');
  return null;
}

/* IBAN ------------------------------------------------------------------------------------------ */

/** IBAN lengths for the SEPA countries (ISO 13616 registry). Unknown countries fall back to 15–34. */
export const ibanLengths: Readonly<Record<string, number>> = {
  AD: 24,
  AT: 20,
  BE: 16,
  BG: 22,
  CH: 21,
  CY: 28,
  CZ: 24,
  DE: 22,
  DK: 18,
  EE: 20,
  ES: 24,
  FI: 18,
  FR: 27,
  GB: 22,
  GI: 23,
  GR: 27,
  HR: 21,
  HU: 28,
  IE: 22,
  IS: 26,
  IT: 27,
  LI: 21,
  LT: 20,
  LU: 20,
  LV: 21,
  MC: 27,
  MT: 31,
  NL: 18,
  NO: 15,
  PL: 28,
  PT: 25,
  RO: 24,
  SE: 24,
  SI: 19,
  SK: 24,
  SM: 27,
  VA: 22,
};

export const IBAN_MAX_LENGTH = 34;

/** Letters and digits only, upper case, at most 34. */
export function normalizeIban(input: string): string {
  return input
    .toUpperCase()
    .replace(/[^A-Z0-9]/g, '')
    .slice(0, IBAN_MAX_LENGTH);
}

/** «GR1601101250000000012300695» → «GR16 0110 1250 0000 0001 2300 695». */
export function formatIban(raw: string): string {
  return groupEvery(raw, 4);
}

/** ISO 7064 mod 97-10 over the rearranged IBAN (letters A=10 … Z=35); valid when the remainder is 1. */
export function ibanMod97(raw: string): number {
  const rearranged = raw.slice(4) + raw.slice(0, 4);
  let remainder = 0;
  for (const ch of rearranged) {
    const value = /[A-Z]/.test(ch) ? ch.charCodeAt(0) - 55 : Number(ch);
    // Fold digit by digit (two digits for letters) to stay within safe integers.
    remainder = value >= 10 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
  }
  return remainder;
}

export function validateIban(raw: string): IdentifierIssue | null {
  const country = raw.slice(0, 2);
  if (!/^[A-Z]{2}\d{2}/.test(raw)) return error('country');
  const expected = ibanLengths[country];
  if (expected !== undefined && raw.length !== expected) {
    return error('length', { country, length: expected });
  }
  if (expected === undefined && (raw.length < 15 || raw.length > IBAN_MAX_LENGTH)) {
    return error('lengthRange', { min: 15, max: IBAN_MAX_LENGTH });
  }
  if (ibanMod97(raw) !== 1) return error('checksum');
  return null;
}

/* Greek licence plate --------------------------------------------------------------------------- */

/** The 14 letters shared by the Greek and Latin alphabets, the only ones used on Greek plates. */
export const PLATE_LETTERS = 'ΑΒΕΖΗΙΚΜΝΟΡΤΥΧ';

/** Latin lookalikes typed on a Latin keyboard, converted to the Greek capital. */
const LATIN_TO_GREEK: Readonly<Record<string, string>> = {
  A: 'Α',
  B: 'Β',
  E: 'Ε',
  Z: 'Ζ',
  H: 'Η',
  I: 'Ι',
  K: 'Κ',
  M: 'Μ',
  N: 'Ν',
  O: 'Ο',
  P: 'Ρ',
  T: 'Τ',
  Y: 'Υ',
  X: 'Χ',
};

export interface NormalizedPlate {
  value: string;
  /** True when at least one Latin lookalike was converted to Greek (the field shows a 1 s hint). */
  converted: boolean;
}

/**
 * Upper-cases (without tonos), converts Latin lookalikes to Greek capitals and drops separators. Other
 * letters are kept so that validation can explain them. At most 7 characters.
 */
export function normalizePlate(input: string): NormalizedPlate {
  let converted = false;
  let value = '';
  const upper = input.normalize('NFD').replace(/\p{M}/gu, '').toLocaleUpperCase('el-GR');
  for (const ch of upper) {
    if (/[\s\-\u2013.·]/.test(ch)) continue;
    const greek = LATIN_TO_GREEK[ch];
    if (greek !== undefined) {
      converted = true;
      value += greek;
    } else {
      value += ch;
    }
  }
  return { value: value.slice(0, 7), converted };
}

/** «ΙΚΧ1234» → «ΙΚΧ-1234»; the hyphen appears once the digits start. */
export function formatPlate(raw: string): string {
  const match = /^(\D{1,3})(\d.*)$/u.exec(raw);
  return match ? `${match[1] ?? ''}-${match[2] ?? ''}` : raw;
}

export function validatePlate(raw: string): IdentifierIssue | null {
  const letters = raw.replace(/\d/g, '');
  // Greek letters and digits are single UTF-16 code units, so a per-character test is safe here.
  if (/[^ΑΒΕΖΗΙΚΜΝΟΡΤΥΧ]/u.test(letters)) {
    return error('letters', { letters: 'Α Β Ε Ζ Η Ι Κ Μ Ν Ο Ρ Τ Υ Χ' });
  }
  if (!/^[ΑΒΕΖΗΙΚΜΝΟΡΤΥΧ]{3}\d{4}$/u.test(raw)) return error('format');
  return null;
}

/* VIN ------------------------------------------------------------------------------------------- */

export const VIN_LENGTH = 17;

/** Letters and digits only, upper case, at most 17 (I, O and Q are kept so validation can explain them). */
export function normalizeVin(input: string): string {
  return input
    .toUpperCase()
    .replace(/[^A-Z0-9]/g, '')
    .slice(0, VIN_LENGTH);
}

const VIN_VALUES: Readonly<Record<string, number>> = {
  A: 1,
  B: 2,
  C: 3,
  D: 4,
  E: 5,
  F: 6,
  G: 7,
  H: 8,
  J: 1,
  K: 2,
  L: 3,
  M: 4,
  N: 5,
  P: 7,
  R: 9,
  S: 2,
  T: 3,
  U: 4,
  V: 5,
  W: 6,
  X: 7,
  Y: 8,
  Z: 9,
};
const VIN_WEIGHTS = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2] as const;

/** ISO 3779 / North American check digit (position 9): `0`–`9` or `X`. */
export function vinCheckDigit(raw: string): string {
  let sum = 0;
  for (let i = 0; i < VIN_LENGTH; i++) {
    const ch = raw[i] ?? '0';
    const value = /\d/.test(ch) ? Number(ch) : (VIN_VALUES[ch] ?? 0);
    sum += value * (VIN_WEIGHTS[i] ?? 0);
  }
  const remainder = sum % 11;
  return remainder === 10 ? 'X' : String(remainder);
}

/**
 * Length and characters are errors. The check digit is only a **warning**: most EU manufacturers do not
 * use it (Part 2 §4.7).
 */
export function validateVin(raw: string): IdentifierIssue | null {
  if (/[IOQ]/.test(raw)) return error('chars');
  if (raw.length !== VIN_LENGTH) return error('length', { length: VIN_LENGTH });
  if (raw[8] !== vinCheckDigit(raw)) return { code: 'checkDigit', severity: 'warning' };
  return null;
}

/* Policy, claim and invoice references ---------------------------------------------------------- */

/** Upper case (Greek without tonos), spaces removed; letters, digits, `-` and `/` kept. At most 40. */
export function normalizeReference(input: string): string {
  return input
    .normalize('NFD')
    .replace(/\p{M}/gu, '')
    .toLocaleUpperCase('el-GR')
    .replace(/[^\p{L}\d\-/]/gu, '')
    .slice(0, 40);
}

/** Validates against the scheme pattern from pack data, when one is given. */
export function validateReference(raw: string, pattern?: RegExp): IdentifierIssue | null {
  if (pattern && !pattern.test(raw)) return error('format');
  return null;
}

/* Shared --------------------------------------------------------------------------------------- */

function groupEvery(raw: string, size: number): string {
  const groups: string[] = [];
  for (let i = 0; i < raw.length; i += size) groups.push(raw.slice(i, i + size));
  return groups.join(' ');
}

export interface IdentifierCodec {
  normalize: (input: string) => string;
  format: (raw: string) => string;
  validate: (raw: string, pattern?: RegExp) => IdentifierIssue | null;
  /** Maximum raw length (for `maxLength` hints). */
  maxLength: number;
}

/** One codec per kind, for the field component. */
export const identifierCodecs: Readonly<Record<IdentifierKind, IdentifierCodec>> = {
  afm: { normalize: normalizeAfm, format: formatAfm, validate: validateAfm, maxLength: AFM_LENGTH },
  iban: {
    normalize: normalizeIban,
    format: formatIban,
    validate: validateIban,
    maxLength: IBAN_MAX_LENGTH,
  },
  plate: {
    normalize: (input) => normalizePlate(input).value,
    format: formatPlate,
    validate: validatePlate,
    maxLength: 7,
  },
  vin: {
    normalize: normalizeVin,
    format: (raw) => raw,
    validate: validateVin,
    maxLength: VIN_LENGTH,
  },
  reference: {
    normalize: normalizeReference,
    format: (raw) => raw,
    validate: validateReference,
    maxLength: 40,
  },
};
