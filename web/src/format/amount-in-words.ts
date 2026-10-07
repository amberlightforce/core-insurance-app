/**
 * Amount in words for payment approvals and maker-checker screens (Part 2 §4.6, Part 3 §5.14):
 * «χίλια διακόσια τριάντα τέσσερα ευρώ και πενήντα έξι λεπτά».
 *
 * Greek numerals agree in gender with the noun they count:
 * - the currency noun (ευρώ and λεπτό are neuter: «τρία ευρώ», «τέσσερα λεπτά»; λίρα is feminine:
 *   «τρεις λίρες», «μία λίρα»),
 * - «χιλιάδες» is feminine («τρεις χιλιάδες», «διακόσιες μία χιλιάδες»), while exactly one thousand is an
 *   adjective agreeing with the counted noun («χίλια ευρώ», «χίλιες λίρες»),
 * - «εκατομμύριο» and «δισεκατομμύριο» are neuter («ένα εκατομμύριο», «τρία εκατομμύρια»).
 * «εκατό» becomes «εκατόν» before a following number («εκατόν ένα»).
 *
 * Input is an exact decimal string or a number; arithmetic is done on integer minor units (no floats).
 */
export type Gender = 'masculine' | 'feminine' | 'neuter';

export interface CurrencyWords {
  gender: Gender;
  singular: string;
  plural: string;
  minorGender: Gender;
  minorSingular: string;
  minorPlural: string;
  /** Minor units per major unit as a power of ten (2 → 100). */
  minorDigits: number;
  en: { singular: string; plural: string; minorSingular: string; minorPlural: string };
}

export const currencyWords: Record<string, CurrencyWords> = {
  EUR: {
    gender: 'neuter',
    singular: 'ευρώ',
    plural: 'ευρώ',
    minorGender: 'neuter',
    minorSingular: 'λεπτό',
    minorPlural: 'λεπτά',
    minorDigits: 2,
    en: { singular: 'euro', plural: 'euros', minorSingular: 'cent', minorPlural: 'cents' },
  },
  USD: {
    gender: 'neuter',
    singular: 'δολάριο',
    plural: 'δολάρια',
    minorGender: 'neuter',
    minorSingular: 'σεντ',
    minorPlural: 'σεντ',
    minorDigits: 2,
    en: { singular: 'dollar', plural: 'dollars', minorSingular: 'cent', minorPlural: 'cents' },
  },
  GBP: {
    gender: 'feminine',
    singular: 'λίρα',
    plural: 'λίρες',
    minorGender: 'feminine',
    minorSingular: 'πένα',
    minorPlural: 'πένες',
    minorDigits: 2,
    en: { singular: 'pound', plural: 'pounds', minorSingular: 'penny', minorPlural: 'pence' },
  },
};

const ones: Record<Gender, string[]> = {
  masculine: ['', 'ένας', 'δύο', 'τρεις', 'τέσσερις', 'πέντε', 'έξι', 'επτά', 'οκτώ', 'εννέα'],
  feminine: ['', 'μία', 'δύο', 'τρεις', 'τέσσερις', 'πέντε', 'έξι', 'επτά', 'οκτώ', 'εννέα'],
  neuter: ['', 'ένα', 'δύο', 'τρία', 'τέσσερα', 'πέντε', 'έξι', 'επτά', 'οκτώ', 'εννέα'],
};

const teens: Record<Gender, string[]> = {
  masculine: [
    'δέκα',
    'έντεκα',
    'δώδεκα',
    'δεκατρείς',
    'δεκατέσσερις',
    'δεκαπέντε',
    'δεκαέξι',
    'δεκαεπτά',
    'δεκαοκτώ',
    'δεκαεννέα',
  ],
  feminine: [
    'δέκα',
    'έντεκα',
    'δώδεκα',
    'δεκατρείς',
    'δεκατέσσερις',
    'δεκαπέντε',
    'δεκαέξι',
    'δεκαεπτά',
    'δεκαοκτώ',
    'δεκαεννέα',
  ],
  neuter: [
    'δέκα',
    'έντεκα',
    'δώδεκα',
    'δεκατρία',
    'δεκατέσσερα',
    'δεκαπέντε',
    'δεκαέξι',
    'δεκαεπτά',
    'δεκαοκτώ',
    'δεκαεννέα',
  ],
};

const tens = [
  '',
  '',
  'είκοσι',
  'τριάντα',
  'σαράντα',
  'πενήντα',
  'εξήντα',
  'εβδομήντα',
  'ογδόντα',
  'ενενήντα',
];

const hundredStems = [
  '',
  '',
  'διακόσι',
  'τριακόσι',
  'τετρακόσι',
  'πεντακόσι',
  'εξακόσι',
  'επτακόσι',
  'οκτακόσι',
  'εννιακόσι',
];
const hundredEndings: Record<Gender, string> = { masculine: 'οι', feminine: 'ες', neuter: 'α' };
const chiliaByGender: Record<Gender, string> = {
  masculine: 'χίλιοι',
  feminine: 'χίλιες',
  neuter: 'χίλια',
};

function digitAt(n: number, place: number): number {
  return Math.floor(n / place) % 10;
}

/** 0–999 in words with gender agreement. Returns '' for 0. */
function belowThousand(n: number, gender: Gender): string {
  const words: string[] = [];
  const h = digitAt(n, 100);
  const rest = n % 100;
  if (h === 1) words.push(rest === 0 ? 'εκατό' : 'εκατόν');
  else if (h > 1) words.push(`${hundredStems[h] ?? ''}${hundredEndings[gender]}`);
  if (rest >= 10 && rest < 20) {
    words.push(teens[gender][rest - 10] ?? '');
  } else {
    const t = digitAt(rest, 10);
    const o = rest % 10;
    if (t > 1) words.push(tens[t] ?? '');
    if (o > 0) words.push(ones[gender][o] ?? '');
  }
  return words.join(' ');
}

/** Any non-negative integer below 10^15 in Greek words, agreeing with `gender`. */
export function integerToGreekWords(value: bigint, gender: Gender = 'neuter'): string {
  if (value < 0n) throw new RangeError('Negative values are handled by amountInWords');
  if (value === 0n) return 'μηδέν';
  if (value >= 10n ** 15n) throw new RangeError('Amount too large for words');

  const groups: number[] = [];
  let rest = value;
  while (rest > 0n) {
    groups.push(Number(rest % 1000n));
    rest /= 1000n;
  }
  const [units = 0, thousands = 0, millions = 0, billions = 0, trillions = 0] = groups;
  const words: string[] = [];

  const scale = (n: number, singular: string, plural: string) => {
    if (n === 0) return;
    words.push(n === 1 ? `ένα ${singular}` : `${belowThousand(n, 'neuter')} ${plural}`);
  };
  scale(trillions, 'τρισεκατομμύριο', 'τρισεκατομμύρια');
  scale(billions, 'δισεκατομμύριο', 'δισεκατομμύρια');
  scale(millions, 'εκατομμύριο', 'εκατομμύρια');
  // Exactly one thousand is an adjective agreeing with the counted noun: χίλια ευρώ, χίλιες λίρες.
  if (thousands === 1) words.push(chiliaByGender[gender]);
  else if (thousands > 1) words.push(`${belowThousand(thousands, 'feminine')} χιλιάδες`);
  if (units > 0) words.push(belowThousand(units, gender));

  return words.join(' ');
}

const enOnes = [
  '',
  'one',
  'two',
  'three',
  'four',
  'five',
  'six',
  'seven',
  'eight',
  'nine',
  'ten',
  'eleven',
  'twelve',
  'thirteen',
  'fourteen',
  'fifteen',
  'sixteen',
  'seventeen',
  'eighteen',
  'nineteen',
];
const enTens = [
  '',
  '',
  'twenty',
  'thirty',
  'forty',
  'fifty',
  'sixty',
  'seventy',
  'eighty',
  'ninety',
];

function enBelowThousand(n: number): string {
  const words: string[] = [];
  const h = Math.floor(n / 100);
  const rest = n % 100;
  if (h > 0) words.push(`${enOnes[h] ?? ''} hundred`);
  if (rest > 0) {
    if (h > 0) words.push('and');
    if (rest < 20) words.push(enOnes[rest] ?? '');
    else words.push([enTens[Math.floor(rest / 10)], enOnes[rest % 10]].filter(Boolean).join('-'));
  }
  return words.join(' ');
}

/** British English words: «one thousand two hundred and thirty-four». */
export function integerToEnglishWords(value: bigint): string {
  if (value === 0n) return 'zero';
  if (value < 0n || value >= 10n ** 15n) throw new RangeError('Out of range');
  const names = ['', 'thousand', 'million', 'billion', 'trillion'];
  const words: string[] = [];
  let rest = value;
  let index = 0;
  while (rest > 0n) {
    const group = Number(rest % 1000n);
    if (group > 0) words.unshift([enBelowThousand(group), names[index]].filter(Boolean).join(' '));
    rest /= 1000n;
    index++;
  }
  return words.join(' ');
}

/** Parses a decimal string/number into integer minor units, rejecting extra precision (never rounds). */
export function toMinorUnits(value: number | string, minorDigits: number): bigint {
  const text =
    typeof value === 'number' ? value.toFixed(minorDigits) : value.trim().replace('−', '-');
  const match = /^([+-])?(\d+)(?:\.(\d+))?$/.exec(text);
  if (!match) throw new RangeError(`Not a decimal amount: ${String(value)}`);
  const [, sign, whole = '0', fraction = ''] = match;
  if (fraction.length > minorDigits && /[1-9]/.test(fraction.slice(minorDigits))) {
    throw new RangeError(`More than ${String(minorDigits)} decimals: ${String(value)}`);
  }
  const minor =
    BigInt(whole) * 10n ** BigInt(minorDigits) +
    BigInt(fraction.slice(0, minorDigits).padEnd(minorDigits, '0') || '0');
  return sign === '-' ? -minor : minor;
}

export interface AmountInWordsOptions {
  currency?: string;
  language?: 'el' | 'en';
  /** Capitalise the first letter («Δώδεκα χιλιάδες …») for standalone display. */
  capitalize?: boolean;
}

/**
 * «χίλια διακόσια τριάντα τέσσερα ευρώ και πενήντα έξι λεπτά» for 1234.56 EUR.
 * Zero major units with cents: «πενήντα λεπτά». Negative amounts start with «μείον».
 */
export function amountInWords(value: number | string, options: AmountInWordsOptions = {}): string {
  const { currency = 'EUR', language = 'el', capitalize = false } = options;
  const words = currencyWords[currency];
  if (!words) throw new RangeError(`No words defined for currency ${currency}`);
  const minor = toMinorUnits(value, words.minorDigits);
  const negative = minor < 0n;
  const absolute = negative ? -minor : minor;
  const base = 10n ** BigInt(words.minorDigits);
  const major = absolute / base;
  const cents = absolute % base;

  let text: string;
  if (language === 'en') {
    const majorText = `${integerToEnglishWords(major)} ${major === 1n ? words.en.singular : words.en.plural}`;
    const centsText = `${integerToEnglishWords(cents)} ${cents === 1n ? words.en.minorSingular : words.en.minorPlural}`;
    if (cents === 0n) text = majorText;
    else if (major === 0n) text = centsText;
    else text = `${majorText} and ${centsText}`;
    if (negative) text = `minus ${text}`;
  } else {
    const majorText = `${integerToGreekWords(major, words.gender)} ${major === 1n ? words.singular : words.plural}`;
    const centsText = `${integerToGreekWords(cents, words.minorGender)} ${
      cents === 1n ? words.minorSingular : words.minorPlural
    }`;
    if (cents === 0n) text = majorText;
    else if (major === 0n) text = centsText;
    else text = `${majorText} και ${centsText}`;
    if (negative) text = `μείον ${text}`;
  }

  return capitalize
    ? text.charAt(0).toLocaleUpperCase(language === 'el' ? 'el-GR' : 'en-GB') + text.slice(1)
    : text;
}
