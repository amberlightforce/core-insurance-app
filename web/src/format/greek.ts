/**
 * Greek text rules in code, not CSS (Part 1 §2.3.4, Part 4 §11.8).
 */

const COMBINING_ACUTE = '́';
const COMBINING_PERISPOMENI = '͂';
const COMBINING_DIAERESIS = '̈';
const YPOGEGRAMMENI = 'ͅ';

/** Accents and breathings removed in capitals: grave, acute (tonos), psili, dasia, perispomeni. */
const STRIPPED_MARKS = /[̀́̓̔͂]/g;

const GREEK_VOWELS = 'αεηιουωΑΕΗΙΟΥΩ';

function isGreekVowel(ch: string | undefined): boolean {
  return ch !== undefined && GREEK_VOWELS.includes(ch);
}

function isIotaOrUpsilon(ch: string | undefined): boolean {
  return ch === 'ι' || ch === 'υ' || ch === 'Ι' || ch === 'Υ';
}

/**
 * Upper-cases Greek correctly: the tonos is dropped, a dialytika is added where the dropped tonos marked a
 * non-diphthong (Μάιος → ΜΑΪΟΣ, άυλος → ΑΫΛΟΣ, ρολόι → ΡΟΛΟΪ), an existing dialytika is kept
 * (πρωτεΐνη → ΠΡΩΤΕΪΝΗ, αϋπνία → ΑΫΠΝΙΑ), polytonic breathings and accents are removed and the ypogegrammeni
 * becomes an adscript Ι (ᾠδή → ΩΙΔΗ), and σ/ς become Σ. Non-Greek text is upper-cased normally.
 *
 * Algorithm (Part 4 §11.8): NFD → flag ι/υ that follow a vowel carrying an acute or perispomeni → strip the
 * marks, ypogegrammeni → ι → `toLocaleUpperCase('el-GR')` → add U+0308 on the flagged ι/υ → NFC.
 */
export function toGreekUpper(text: string, locale = 'el-GR'): string {
  // Work on clusters: a base character followed by its combining marks.
  const clusters = text.normalize('NFD').match(/\P{M}\p{M}*|\p{M}+/gu) ?? [];
  const out: string[] = [];

  for (let i = 0; i < clusters.length; i++) {
    const cluster = clusters[i] ?? '';
    const base = cluster[0];
    const marks = cluster.slice(1);
    let flagged = false;

    if (isIotaOrUpsilon(base) && !marks.includes(COMBINING_DIAERESIS)) {
      const previous = clusters[i - 1] ?? '';
      const previousBase = previous[0];
      const previousAccented =
        previous.includes(COMBINING_ACUTE) || previous.includes(COMBINING_PERISPOMENI);
      // An accent on the vowel before ι/υ means the pair is not a diphthong (ά-ι, ό-ι, ά-υ).
      // A grave or no accent leaves the diphthong reading, so nothing is added.
      if (isGreekVowel(previousBase) && previousAccented && !previous.includes(YPOGEGRAMMENI)) {
        flagged = true;
      }
    }

    let kept = marks.replace(STRIPPED_MARKS, '');
    let adscript = '';
    if (kept.includes(YPOGEGRAMMENI)) {
      kept = kept.replaceAll(YPOGEGRAMMENI, '');
      adscript = 'ι';
    }
    let upper = (base ?? '').toLocaleUpperCase(locale) + kept + adscript.toLocaleUpperCase(locale);
    if (flagged) upper = upper.slice(0, 1) + COMBINING_DIAERESIS + upper.slice(1);
    out.push(upper);
  }

  return out.join('').normalize('NFC');
}

/**
 * Lower-cases Greek with the final sigma at word ends (Σ → ς when no letter follows), e.g.
 * «ΟΔΟΣ» → «οδος», «ΣΟΦΙΑ ΣΤΗΝ ΑΘΗΝΑ» → «σοφια στην αθηνα». Accents cannot be restored from capitals.
 */
export function toGreekLower(text: string, locale = 'el-GR'): string {
  return applyFinalSigma(text.toLocaleLowerCase(locale));
}

/** Replaces σ with ς at the end of a word and ς with σ inside a word. */
export function applyFinalSigma(text: string): string {
  return text.replace(/[σς](?=(\p{M}*)(\P{L}|$))|[σς]/gu, (match, _marks, _next, offset: number) => {
    const rest = text.slice(offset + match.length);
    const nextLetter = /^\p{M}*(\p{L})/u.exec(rest);
    const previousIsLetter = offset > 0 && /\p{L}\p{M}*$/u.test(text.slice(0, offset));
    if (!nextLetter && previousIsLetter) return 'ς';
    return 'σ';
  });
}

/** Removes Greek accents, breathings and dialytika (NFD strip), keeping the letters. */
export function stripGreekDiacritics(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').normalize('NFC');
}

/** True when the text contains at least one Greek letter (U+0370–03FF, U+1F00–1FFF). */
export function hasGreek(text: string): boolean {
  return /[Ͱ-Ͽἀ-῿]/.test(text);
}

/** Greek collation for sorting: ά sorts with α, ΑΣΦ-2 before ΑΣΦ-10 (Part 2 §4.35). */
export const greekCollator = new Intl.Collator('el', { sensitivity: 'base', numeric: true });

export function compareGreek(a: string, b: string): number {
  return greekCollator.compare(a, b);
}
