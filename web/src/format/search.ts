/**
 * Greek search: one normaliser and matcher shared by Combobox, MultiCombobox, the command palette and table
 * search (Part 2 §4.4, DESIGN-A §0.3).
 *
 * - Accent- and case-insensitive: NFD, strip U+0300–U+036F, `toLocaleLowerCase('el')`, ς → σ, collapsed
 *   whitespace.
 * - Greeklish both ways: ELOT 743 (type 2, aligned with ISO 843) for Greek → Latin, plus a loose reverse map
 *   for the informal Greeklish people actually type («8» for θ, «h» for η or χ, «w» for ω, «u» for υ/ου…).
 *   «papad» finds «Παπαδόπουλος» and «Παπαδ» finds «Papadopoulos».
 * - Numeric queries find identifier digits («4471» → «ΑΣΦ-2026-004471»).
 * - Match ranges point into the ORIGINAL candidate string, so callers can highlight it.
 */

/** A matched span in the original candidate: UTF-16 indices, `end` exclusive. */
export interface MatchRange {
  start: number;
  end: number;
}

export interface MatchResult {
  /** Sorted, merged ranges in the original candidate for highlighting. */
  ranges: MatchRange[];
  /** Every query word matched at the start of a word (rank these first). */
  wordPrefix: boolean;
  /** Higher is better: word-start matches and direct (non-transliterated) matches score higher. */
  score: number;
}

/** A normalised string plus, for each of its characters, the span of the original text it came from. */
interface Mapped {
  text: string;
  starts: number[];
  ends: number[];
}

const COMBINING = /[\u0300-\u036f]/g;
const GREEK_LETTER = /[\u0370-\u03ff\u1f00-\u1fff]/u;
const LATIN_LETTER = /[a-z]/;
const ALNUM = /[\p{L}\p{N}]/u;
const WHITESPACE = /\s/u;

function foldChar(ch: string): string {
  return ch.normalize('NFD').replace(COMBINING, '').toLocaleLowerCase('el').replace(/ς/g, 'σ');
}

function normalizeMapped(text: string): Mapped {
  const out: Mapped = { text: '', starts: [], ends: [] };
  let chars = '';
  let pendingSpace: number | null = null;
  for (let i = 0; i < text.length;) {
    const cp = text.codePointAt(i) ?? 0;
    const ch = String.fromCodePoint(cp);
    const len = ch.length;
    if (WHITESPACE.test(ch)) {
      if (chars.length > 0 && pendingSpace === null) pendingSpace = i;
      i += len;
      continue;
    }
    const folded = foldChar(ch);
    if (folded.length > 0) {
      if (pendingSpace !== null) {
        chars += ' ';
        out.starts.push(pendingSpace);
        out.ends.push(pendingSpace + 1);
        pendingSpace = null;
      }
      for (const c of folded) {
        chars += c;
        out.starts.push(i);
        out.ends.push(i + len);
      }
    }
    i += len;
  }
  out.text = chars;
  return out;
}

/**
 * Search normalisation: NFD, strip combining marks U+0300–U+036F, `toLocaleLowerCase('el')`, ς → σ, collapse
 * and trim whitespace. «Γεώργιος  ΠΑΠΑΔΌΠΟΥΛΟΣ» → «γεωργιοσ παπαδοπουλοσ».
 *
 * Client-side only (in-memory lists, palette, table search). It keeps punctuation, so it is NOT the backend
 * search key (`NameTransliterator.searchVariants`); never send it to the server as an index key.
 */
export function normalizeForSearch(text: string): string {
  return normalizeMapped(text).text;
}

/* ------------------------------------------------------------------------------------------------------ */
/* ELOT 743 (Greek → Latin)                                                                                 */
/* ------------------------------------------------------------------------------------------------------ */

interface Unit {
  /** Lower-case base character (accents removed); ς is folded to σ. */
  base: string;
  upper: boolean;
  diaeresis: boolean;
  accent: boolean;
  greek: boolean;
  letter: boolean;
  start: number;
  end: number;
}

const ELOT_SINGLE: Readonly<Record<string, string>> = {
  α: 'a',
  β: 'v',
  γ: 'g',
  δ: 'd',
  ε: 'e',
  ζ: 'z',
  η: 'i',
  θ: 'th',
  ι: 'i',
  κ: 'k',
  λ: 'l',
  μ: 'm',
  ν: 'n',
  ξ: 'x',
  ο: 'o',
  π: 'p',
  ρ: 'r',
  σ: 's',
  τ: 't',
  υ: 'y',
  φ: 'f',
  χ: 'ch',
  ψ: 'ps',
  ω: 'o',
};

const GREEK_VOWELS = 'αεηιουω';
/** Voiced consonants: αυ/ευ/ηυ become av/ev/iv before these (and before vowels). */
const VOICED = 'βγδζλμνρ';

function toUnits(text: string): Unit[] {
  const units: Unit[] = [];
  for (let i = 0; i < text.length;) {
    const ch = String.fromCodePoint(text.codePointAt(i) ?? 0);
    const len = ch.length;
    const nfd = Array.from(ch.normalize('NFD'));
    const base = nfd[0] ?? ch;
    const marks = nfd.slice(1).join('');
    const last = units[units.length - 1];
    if (/^\p{M}$/u.test(base) && last) {
      // A separate combining mark in the source (already-decomposed text) belongs to the previous unit.
      if (base === '\u0308') last.diaeresis = true;
      else if (/[\u0300\u0301\u0342]/.test(base)) last.accent = true;
      last.end = i + len;
      i += len;
      continue;
    }
    const lower = base.toLocaleLowerCase('el');
    units.push({
      base: lower === 'ς' ? 'σ' : lower,
      upper: lower !== base,
      diaeresis: marks.includes('\u0308'),
      accent: /[\u0300\u0301\u0342]/.test(marks),
      greek: GREEK_LETTER.test(base),
      letter: /\p{L}/u.test(base),
      start: i,
      end: i + len,
    });
    i += len;
  }
  return units;
}

interface Segment {
  text: string;
  /** Index of the first and one-past-last unit covered. */
  from: number;
  to: number;
}

function isWordStart(units: Unit[], i: number): boolean {
  const prev = units[i - 1];
  return !prev?.letter;
}

function isWordEnd(units: Unit[], i: number): boolean {
  const next = units[i + 1];
  return !next?.letter;
}

function transliterateUnits(units: Unit[]): Segment[] {
  const segments: Segment[] = [];
  for (let i = 0; i < units.length;) {
    const u = units[i];
    if (!u) break;
    if (!u.greek) {
      segments.push({ text: '', from: i, to: i + 1 });
      i += 1;
      continue;
    }
    const next = units[i + 1];
    const nextGreek = next?.greek === true ? next.base : '';
    const pair = u.base + nextGreek;

    // Diphthongs with υ (unless υ carries a dialytika, or the first vowel carries the accent alone).
    if (nextGreek === 'υ' && next && !next.diaeresis && !(u.accent && !next.accent)) {
      if (u.base === 'ο') {
        segments.push({ text: 'ou', from: i, to: i + 2 });
        i += 2;
        continue;
      }
      if (u.base === 'α' || u.base === 'ε' || u.base === 'η') {
        const after = units[i + 2];
        const afterBase = after?.greek === true ? after.base : '';
        const voiced =
          afterBase !== '' && (GREEK_VOWELS.includes(afterBase) || VOICED.includes(afterBase));
        const first = ELOT_SINGLE[u.base] ?? '';
        segments.push({ text: first + (voiced ? 'v' : 'f'), from: i, to: i + 2 });
        i += 2;
        continue;
      }
    }

    // ELOT 743 (D-FE-25, same vectors as the backend ElotTransliterator): μπ → b at the start or end of a
    // word and mp inside; ντ → nt; γκ → gk; γγ → ng; γξ → nx; γχ → nch.
    let digraph: string | null = null;
    switch (pair) {
      case 'μπ':
        digraph = isWordStart(units, i) || isWordEnd(units, i + 1) ? 'b' : 'mp';
        break;
      case 'ντ':
        digraph = 'nt';
        break;
      case 'γγ':
        digraph = 'ng';
        break;
      case 'γκ':
        digraph = 'gk';
        break;
      case 'γξ':
        digraph = 'nx';
        break;
      case 'γχ':
        digraph = 'nch';
        break;
      default:
        break;
    }
    if (digraph !== null) {
      segments.push({ text: digraph, from: i, to: i + 2 });
      i += 2;
      continue;
    }
    segments.push({ text: ELOT_SINGLE[u.base] ?? u.base, from: i, to: i + 1 });
    i += 1;
  }
  return segments;
}

/** True when every letter of the word around unit `i` is upper case (and the word has 2+ letters). */
function wordAllCaps(units: Unit[], i: number): boolean {
  let start = i;
  while (start > 0 && units[start - 1]?.letter) start -= 1;
  let end = i;
  while (end < units.length - 1 && units[end + 1]?.letter) end += 1;
  if (end === start) {
    // A single capital letter: treat as all-caps only inside an all-caps phrase.
    const neighbour = units[end + 2] ?? units[start - 2];
    return neighbour?.letter === true && neighbour.upper;
  }
  for (let k = start; k <= end; k++) {
    if (!units[k]?.upper) return false;
  }
  return true;
}

/**
 * Greek → Latin per ELOT 743 (type 2 transliteration, aligned with ISO 843). Accents are dropped; case is
 * kept («Θεσσαλονίκη» → «Thessaloniki», «ΘΕΣΣΑΛΟΝΙΚΗ» → «THESSALONIKI»). Non-Greek text passes through.
 *
 * - αυ/ευ/ηυ → av/ev/iv before vowels and voiced consonants, af/ef/if before voiceless consonants and at the
 *   end of a word; ου → ou (a dialytika breaks the diphthong: «αϋπνία» → «aypnia»).
 * - μπ → b at the start or end of a word, mp inside; ντ → nt; γκ → gk; γγ → ng; γξ → nx; γχ → nch.
 * These match the backend `ElotTransliterator` vectors (D-FE-25); the loose matcher below still accepts
 * informal spellings (d for ντ, g for γκ, …) when searching.
 * - θ → th, χ → ch, ψ → ps, η → i, υ → y, ω → o, ξ → x, φ → f, β → v.
 */
export function greekToGreeklish(text: string): string {
  const units = toUnits(text);
  const segments = transliterateUnits(units);
  let out = '';
  for (const seg of segments) {
    const first = units[seg.from];
    const last = units[seg.to - 1];
    if (!first || !last) continue;
    if (!first.greek) {
      out += text.slice(first.start, last.end);
      continue;
    }
    if (!first.upper) {
      out += seg.text;
    } else if (wordAllCaps(units, seg.from)) {
      out += seg.text.toUpperCase();
    } else {
      out += seg.text.charAt(0).toUpperCase() + seg.text.slice(1);
    }
  }
  return out;
}

/* ------------------------------------------------------------------------------------------------------ */
/* Greeklish (Latin → Greek)                                                                                */
/* ------------------------------------------------------------------------------------------------------ */

/** Multi-letter Greeklish tokens, longest first, with their most likely Greek reading. */
const GREEKLISH_MULTI: readonly (readonly [string, string])[] = [
  ['nch', 'γχ'],
  ['th', 'θ'],
  ['ch', 'χ'],
  ['kh', 'χ'],
  ['ps', 'ψ'],
  ['ks', 'ξ'],
  ['ph', 'φ'],
  ['ou', 'ου'],
  ['mp', 'μπ'],
  ['nt', 'ντ'],
  ['gk', 'γκ'],
  ['gg', 'γγ'],
  ['ng', 'γγ'],
  ['nk', 'γκ'],
  ['nx', 'γξ'],
];

const GREEKLISH_SINGLE: Readonly<Record<string, string>> = {
  a: 'α',
  b: 'μπ',
  c: 'κ',
  d: 'δ',
  e: 'ε',
  f: 'φ',
  g: 'γ',
  h: 'η',
  i: 'ι',
  k: 'κ',
  l: 'λ',
  m: 'μ',
  n: 'ν',
  o: 'ο',
  p: 'π',
  q: 'κ',
  r: 'ρ',
  s: 'σ',
  t: 'τ',
  u: 'υ',
  v: 'β',
  w: 'ω',
  x: 'ξ',
  y: 'υ',
  z: 'ζ',
  '8': 'θ',
};

const LATIN_VOWELS = 'aeiouyw';
const LATIN_VOICED_START = ['v', 'g', 'd', 'z', 'l', 'm', 'n', 'r', 'b'];

function convertGreeklishWord(word: string): string {
  const lower = word.toLowerCase();
  let out = '';
  let i = 0;
  while (i < lower.length) {
    const ch = lower.charAt(i);
    // av/ev before a vowel or voiced consonant, af/ef before a voiceless consonant or the end → αυ/ευ.
    if ((ch === 'a' || ch === 'e') && (lower[i + 1] === 'v' || lower[i + 1] === 'f')) {
      const after = lower.charAt(i + 2);
      const isV = lower[i + 1] === 'v';
      const voicedNext =
        after !== '' && (LATIN_VOWELS.includes(after) || LATIN_VOICED_START.includes(after));
      if (isV === voicedNext) {
        out += ch === 'a' ? 'αυ' : 'ευ';
        i += 2;
        continue;
      }
    }
    const multi = GREEKLISH_MULTI.find(([lat]) => lower.startsWith(lat, i));
    if (multi) {
      out += multi[1];
      i += multi[0].length;
      continue;
    }
    out += GREEKLISH_SINGLE[ch] ?? ch;
    i += 1;
  }
  // Final sigma.
  out = out.replace(/σ$/u, 'ς');
  if (word.length > 1 && word === word.toUpperCase() && /[A-Z]/.test(word)) {
    return out.toLocaleUpperCase('el');
  }
  if (/^[A-Z]/.test(word)) {
    const first = Array.from(out)[0] ?? '';
    // A capital digraph capitalises only its first letter («Mp» → «Μπ»).
    return first.toLocaleUpperCase('el') + out.slice(first.length);
  }
  return out;
}

/**
 * Best-guess Latin → Greek for typed Greeklish (ELOT 743 inverse plus common informal spellings: «8»/«th» →
 * θ, «ch»/«kh» → χ, «ps» → ψ, «ks»/«x» → ξ, «b»/«mp» → μπ, «nt» → ντ, «gk» → γκ, «w» → ω, «h» → η, «u»/«y»
 * → υ, «ou» → ου; av/ev/af/ef → αυ/ευ by the ELOT context rule; a final σ becomes ς). Accents cannot be
 * restored. Greeklish is ambiguous («x» may be ξ or χ, «h» η or χ), so search never relies on this single
 * reading: `matchesQuery` tries every reading.
 */
export function greeklishToGreek(text: string): string {
  return text.replace(/[A-Za-z8]+/g, (word) =>
    // A lone digit is a number, not a θ.
    /^[0-9]+$/.test(word) ? word : convertGreeklishWord(word),
  );
}

/* ------------------------------------------------------------------------------------------------------ */
/* Loose matching                                                                                           */
/* ------------------------------------------------------------------------------------------------------ */

/**
 * Latin ↔ Greek equivalences used for matching (normalised forms: lower case, no accents, σ for ς). Every ELOT
 * 743 output is here, plus informal variants. `afterVowel` pairs apply only when the Greek side follows α, ε
 * or η (the υ of αυ/ευ/ηυ read as v or f).
 */
interface Equivalence {
  latin: string;
  greek: string;
  afterVowel?: boolean;
}

const EQUIVALENCES: readonly Equivalence[] = [
  { latin: 'a', greek: 'α' },
  { latin: 'b', greek: 'β' },
  { latin: 'b', greek: 'μπ' },
  { latin: 'mp', greek: 'μπ' },
  { latin: 'v', greek: 'β' },
  { latin: 'w', greek: 'β' },
  { latin: 'g', greek: 'γ' },
  { latin: 'g', greek: 'γκ' },
  { latin: 'gk', greek: 'γκ' },
  { latin: 'gg', greek: 'γγ' },
  { latin: 'ng', greek: 'γγ' },
  { latin: 'nk', greek: 'γκ' },
  { latin: 'nx', greek: 'γξ' },
  { latin: 'nks', greek: 'γξ' },
  { latin: 'nch', greek: 'γχ' },
  { latin: 'nh', greek: 'γχ' },
  { latin: 'y', greek: 'γ' },
  { latin: 'd', greek: 'δ' },
  { latin: 'dh', greek: 'δ' },
  { latin: 'd', greek: 'ντ' },
  { latin: 'nt', greek: 'ντ' },
  { latin: 'e', greek: 'ε' },
  { latin: 'e', greek: 'αι' },
  { latin: 'z', greek: 'ζ' },
  { latin: 'i', greek: 'η' },
  { latin: 'h', greek: 'η' },
  { latin: 'i', greek: 'ι' },
  { latin: 'i', greek: 'υ' },
  { latin: 'i', greek: 'ει' },
  { latin: 'i', greek: 'οι' },
  { latin: 'th', greek: 'θ' },
  { latin: '8', greek: 'θ' },
  { latin: 'k', greek: 'κ' },
  { latin: 'c', greek: 'κ' },
  { latin: 'q', greek: 'κ' },
  { latin: 'l', greek: 'λ' },
  { latin: 'm', greek: 'μ' },
  { latin: 'n', greek: 'ν' },
  { latin: 'x', greek: 'ξ' },
  { latin: 'ks', greek: 'ξ' },
  { latin: '3', greek: 'ξ' },
  { latin: 'o', greek: 'ο' },
  { latin: 'o', greek: 'ω' },
  { latin: 'w', greek: 'ω' },
  { latin: 'p', greek: 'π' },
  { latin: 'r', greek: 'ρ' },
  { latin: 's', greek: 'σ' },
  { latin: 't', greek: 'τ' },
  { latin: 'y', greek: 'υ' },
  { latin: 'u', greek: 'υ' },
  { latin: 'u', greek: 'ου' },
  { latin: 'ou', greek: 'ου' },
  { latin: 'f', greek: 'φ' },
  { latin: 'ph', greek: 'φ' },
  { latin: 'ch', greek: 'χ' },
  { latin: 'kh', greek: 'χ' },
  { latin: 'h', greek: 'χ' },
  { latin: 'x', greek: 'χ' },
  { latin: 'ps', greek: 'ψ' },
  { latin: 'v', greek: 'υ', afterVowel: true },
  { latin: 'f', greek: 'υ', afterVowel: true },
];

type Direction = 'latinQuery' | 'greekQuery';

function equivalenceSides(eq: Equivalence, direction: Direction): [query: string, cand: string] {
  return direction === 'latinQuery' ? [eq.latin, eq.greek] : [eq.greek, eq.latin];
}

/**
 * Tries to consume the whole `query` (from `qi`) against `cand` (from `cj`) through literal characters and the
 * equivalence table. Returns the end index in `cand`, or −1. The last query letters may be the beginning of a
 * multi-letter token («kat» already matches «καθ…», «Παπαδοπουλοσ μ» matches «… mp…»).
 */
function looseMatchFrom(query: string, cand: string, start: number, direction: Direction): number {
  const memo = new Map<number, number>();
  const width = cand.length + 1;

  const greekPrev = (qi: number, cj: number): string =>
    direction === 'latinQuery' ? cand.charAt(cj - 1) : query.charAt(qi - 1);

  const rec = (qi: number, cj: number): number => {
    if (qi === query.length) return cj;
    const key = qi * width + cj;
    const cached = memo.get(key);
    if (cached !== undefined) return cached;
    memo.set(key, -1);
    let result = -1;
    if (cj < cand.length && query.charAt(qi) === cand.charAt(cj)) {
      result = rec(qi + 1, cj + 1);
    }
    if (result < 0) {
      for (const eq of EQUIVALENCES) {
        const [qs, cs] = equivalenceSides(eq, direction);
        if (eq.afterVowel === true && !'αεη'.includes(greekPrev(qi, cj) || '#')) continue;
        if (!cand.startsWith(cs, cj)) continue;
        if (query.startsWith(qs, qi)) {
          result = rec(qi + qs.length, cj + cs.length);
        } else {
          const rest = query.slice(qi);
          // A partial final token: the query ends in the middle of this token.
          if (qs.length > rest.length && qs.startsWith(rest)) result = cj + cs.length;
        }
        if (result >= 0) break;
      }
    }
    memo.set(key, result);
    return result;
  };
  return rec(0, start);
}

function isWordStartAt(text: string, i: number): boolean {
  return i === 0 || !ALNUM.test(text.charAt(i - 1));
}

interface TokenMatch {
  start: number;
  end: number;
  wordStart: boolean;
  direct: boolean;
}

/** Finds `token` in `cand` (normalised): word starts first, then (for 2+ characters) anywhere. */
function findToken(token: string, cand: Mapped): TokenMatch | null {
  const text = cand.text;
  const allowInside = Array.from(token).length >= 2;

  // 1. Direct (same script, accent- and case-insensitive).
  let firstInside: TokenMatch | null = null;
  for (let from = 0; ;) {
    const at = text.indexOf(token, from);
    if (at < 0) break;
    if (isWordStartAt(text, at))
      return { start: at, end: at + token.length, wordStart: true, direct: true };
    firstInside ??= { start: at, end: at + token.length, wordStart: false, direct: true };
    from = at + 1;
  }

  // 2. Digits ignore separators inside identifiers («2026004471» → «ΑΣΦ-2026-004471»).
  if (/^\d+$/.test(token) && firstInside === null) {
    const digitIdx: number[] = [];
    let digits = '';
    for (let k = 0; k < text.length; k++) {
      const c = text.charAt(k);
      if (/\d/.test(c)) {
        digits += c;
        digitIdx.push(k);
      }
    }
    const at = digits.indexOf(token);
    const startIdx = digitIdx[at];
    const endIdx = digitIdx[at + token.length - 1];
    if (at >= 0 && startIdx !== undefined && endIdx !== undefined) {
      return {
        start: startIdx,
        end: endIdx + 1,
        wordStart: isWordStartAt(text, startIdx),
        direct: true,
      };
    }
  }

  // 3. Across scripts (Greeklish ↔ Greek).
  const queryGreek = GREEK_LETTER.test(token);
  const queryLatin = /[a-z]/.test(token);
  if (queryGreek !== queryLatin) {
    const direction: Direction = queryGreek ? 'greekQuery' : 'latinQuery';
    const startsOn = direction === 'latinQuery' ? GREEK_LETTER : LATIN_LETTER;
    let looseInside: TokenMatch | null = null;
    for (let j = 0; j < text.length; j++) {
      if (!startsOn.test(text.charAt(j))) continue;
      const wordStart = isWordStartAt(text, j);
      if (!wordStart && (!allowInside || looseInside !== null)) continue;
      const end = looseMatchFrom(token, text, j, direction);
      if (end > j) {
        if (wordStart) return { start: j, end, wordStart: true, direct: false };
        looseInside = { start: j, end, wordStart: false, direct: false };
      }
    }
    if (allowInside && firstInside) return firstInside;
    if (allowInside && looseInside) return looseInside;
    return null;
  }
  return allowInside ? firstInside : null;
}

function mergeRanges(ranges: MatchRange[]): MatchRange[] {
  const sorted = [...ranges].sort((a, b) => a.start - b.start || a.end - b.end);
  const out: MatchRange[] = [];
  for (const r of sorted) {
    const last = out[out.length - 1];
    if (last && r.start <= last.end) last.end = Math.max(last.end, r.end);
    else out.push({ ...r });
  }
  return out;
}

/**
 * Matches `query` against `candidate`. Every query word must match: as a prefix of a word, or (2+ characters)
 * anywhere inside, after normalising both sides; Latin queries match Greek candidates through ELOT 743 and
 * informal Greeklish, Greek queries match Latin candidates, and digits match identifier digits. Returns null
 * when there is no match; an empty query matches with no ranges.
 */
export function matchesQuery(candidate: string, query: string): MatchResult | null {
  const q = normalizeForSearch(query);
  if (q === '') return { ranges: [], wordPrefix: true, score: 0 };
  const cand = normalizeMapped(candidate);
  const ranges: MatchRange[] = [];
  let wordPrefix = true;
  let score = 0;
  for (const token of q.split(' ')) {
    const m = findToken(token, cand);
    if (!m) return null;
    const startOrig = cand.starts[m.start];
    const endOrig = cand.ends[m.end - 1];
    if (startOrig === undefined || endOrig === undefined) return null;
    ranges.push({ start: startOrig, end: endOrig });
    wordPrefix &&= m.wordStart;
    score += (m.wordStart ? 4 : 1) + (m.direct ? 2 : 0) + (m.start === 0 ? 1 : 0);
  }
  return { ranges: mergeRanges(ranges), wordPrefix, score };
}

/**
 * A filter for React Aria's `defaultFilter` / `filter` props: `(textValue, inputValue) => boolean`.
 */
export function createGreekFilter(): (textValue: string, inputValue: string) => boolean {
  return (textValue, inputValue) => matchesQuery(textValue, inputValue) !== null;
}

export interface SearchHit<T> {
  item: T;
  match: MatchResult;
  /** Index of the text (from `getText`) that matched; its ranges belong to that text. */
  field: number;
}

/**
 * Filters and ranks items for a query (best score first, original order kept for ties). `getText` returns the
 * searchable texts of an item (label, caption, identifier); the first text that matches supplies the ranges.
 */
export function searchItems<T>(
  items: readonly T[],
  query: string,
  getText: (item: T) => string | readonly string[],
): SearchHit<T>[] {
  const hits: (SearchHit<T> & { index: number })[] = [];
  items.forEach((item, index) => {
    const texts = getText(item);
    const list = typeof texts === 'string' ? [texts] : texts;
    for (let field = 0; field < list.length; field++) {
      const match = matchesQuery(list[field] ?? '', query);
      if (match) {
        hits.push({ item, match, index, field });
        break;
      }
    }
  });
  hits.sort((a, b) => b.match.score - a.match.score || a.field - b.field || a.index - b.index);
  return hits.map(({ item, match, field }) => ({ item, match, field }));
}
