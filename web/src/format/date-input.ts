/**
 * Date entry accelerators for the date fields (Part 2 §4.5, DESIGN-A §7.5, D-FE-08). Parsed on Enter or blur
 * in the field's quick-entry box, with a live preview «→ Πέμπτη, 6 Νοεμβρίου 2026».
 *
 * Accepted (Greek words are accent- and case-insensitive):
 * - dates: `7/10`, `7.10`, `7-10-2026`, `7.10.26`, `07102026`, `071026`, `0710`, `7 10 2026`, a bare day `15`
 * - words: `σ`/`σήμερα`/`t`/`today`, `α`/`αύριο`/`tomorrow`, `χ`/`χθες`/`y`/`yesterday`, `τμ`/`eom` (end of
 *   the month), `αμ`/`som` (start of the month); `s`, `a` and `tm` are σ, α and τμ typed on the Latin layout
 * - offsets from today: `+30`/`-7` (days), `+2w`/`+2εβδ` (weeks), `+6μ`/`+6m` (months), `+1ε`/`+1y` (years)
 */
import { CalendarDate, endOfMonth, startOfMonth } from '@internationalized/date';

export interface ParseDateOptions {
  /** «Today» in Europe/Athens, e.g. `today(ATHENS)`; injected so tests and stories are deterministic. */
  today: CalendarDate;
}

function normalize(text: string): string {
  return text
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLocaleLowerCase('el')
    .replace(/ς/g, 'σ')
    .trim();
}

const WORDS: Readonly<Record<string, (today: CalendarDate) => CalendarDate>> = {
  σ: (t) => t,
  σημερα: (t) => t,
  t: (t) => t,
  today: (t) => t,
  α: (t) => t.add({ days: 1 }),
  αυριο: (t) => t.add({ days: 1 }),
  tomorrow: (t) => t.add({ days: 1 }),
  χ: (t) => t.subtract({ days: 1 }),
  χθεσ: (t) => t.subtract({ days: 1 }),
  y: (t) => t.subtract({ days: 1 }),
  yesterday: (t) => t.subtract({ days: 1 }),
  τμ: (t) => endOfMonth(t),
  eom: (t) => endOfMonth(t),
  // The same keys typed with the Latin layout still active (σ is S, α is A, τμ is TM on a Greek keyboard).
  s: (t) => t,
  a: (t) => t.add({ days: 1 }),
  tm: (t) => endOfMonth(t),
  αμ: (t) => startOfMonth(t),
  som: (t) => startOfMonth(t),
};

type Unit = 'days' | 'weeks' | 'months' | 'years';

const UNITS: readonly (readonly [RegExp, Unit])[] = [
  [/^(?:|d|η|ημ|ημερ[αεη]?σ?|days?)$/u, 'days'],
  [/^(?:w|εβδ|εβδομαδ[αεη]?σ?|weeks?)$/u, 'weeks'],
  [/^(?:m|μ|μην|μηνα?σ?|μηνεσ|months?)$/u, 'months'],
  [/^(?:y|ε|ετ|ετοσ|ετη|χρ|χρονια?|χρονοσ|years?)$/u, 'years'],
];

function fullYear(text: string, today: CalendarDate): number {
  const value = Number(text);
  if (text.length > 2) return value;
  // Two-digit years: within the next 20 years → 20xx, otherwise 19xx («7.10.85» is a birth date).
  const century = 2000 + value;
  return century > today.year + 20 ? 1900 + value : century;
}

function makeDate(day: number, month: number, year: number): CalendarDate | null {
  if (!Number.isInteger(day) || !Number.isInteger(month) || !Number.isInteger(year)) return null;
  if (month < 1 || month > 12 || day < 1 || year < 1 || year > 9999) return null;
  const date = new CalendarDate(year, month, day);
  // CalendarDate constrains out-of-range days (31/2 → 28/2); a constrained date is an invalid entry.
  return date.day === day && date.month === month ? date : null;
}

/**
 * Parses a quick-entry date. Returns null when the text is not a recognised date (the field keeps its
 * value and shows an error).
 */
export function parseDateInput(text: string, { today }: ParseDateOptions): CalendarDate | null {
  const s = normalize(text).replace(/\s+/g, ' ');
  if (s === '') return null;

  const word = WORDS[s.replace(/[.\s]/g, '')];
  if (word) return word(today);

  // Offsets: «+30», «-7», «+6μ», «+1 ε», «−2w».
  const offset = /^([+\-\u2212])\s*(\d{1,4})\s*([\p{L}]*)\.?$/u.exec(s);
  if (offset) {
    const sign = offset[1] === '+' ? 1 : -1;
    const amount = Number(offset[2]) * sign;
    const unitText = offset[3] ?? '';
    const unit = UNITS.find(([re]) => re.test(unitText))?.[1];
    if (!unit) return null;
    return today.add({ [unit]: amount });
  }

  // Separated dates: d/m, d/m/y with «/», «.», «-» or spaces.
  const separated = /^(\d{1,2})[/.\-\s](\d{1,2})(?:[/.\-\s](\d{2}|\d{4}))?\.?$/.exec(s);
  if (separated) {
    const year = separated[3] ? fullYear(separated[3], today) : today.year;
    return makeDate(Number(separated[1]), Number(separated[2]), year);
  }

  // Digits only: ddmmyyyy, ddmmyy, ddmm, or a day of the current month.
  if (/^\d+$/.test(s)) {
    switch (s.length) {
      case 8:
        return makeDate(Number(s.slice(0, 2)), Number(s.slice(2, 4)), Number(s.slice(4)));
      case 6:
        return makeDate(Number(s.slice(0, 2)), Number(s.slice(2, 4)), fullYear(s.slice(4), today));
      case 4:
        return makeDate(Number(s.slice(0, 2)), Number(s.slice(2, 4)), today.year);
      case 1:
      case 2:
        return makeDate(Number(s), today.month, today.year);
      default:
        return null;
    }
  }
  return null;
}

/** The characters that open the quick-entry box when typed on a date field (letters, «+», «-», «−»). */
export function isAcceleratorKey(key: string): boolean {
  return key === '+' || key === '-' || key === '\u2212' || /^\p{L}$/u.test(key);
}
