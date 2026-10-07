import { describe, expect, it } from 'vitest';

import {
  createGreekFilter,
  greekToGreeklish,
  greeklishToGreek,
  matchesQuery,
  normalizeForSearch,
  searchItems,
} from './search';

/** The highlighted substrings of `candidate` for `query`. */
function highlighted(candidate: string, query: string): string[] | null {
  const m = matchesQuery(candidate, query);
  return m ? m.ranges.map((r) => candidate.slice(r.start, r.end)) : null;
}

describe('normalizeForSearch', () => {
  it.each([
    ['Γεώργιος', 'γεωργιοσ'],
    ['ΠΑΠΑΔΌΠΟΥΛΟΣ', 'παπαδοπουλοσ'],
    ['Ασφαλιστής', 'ασφαλιστησ'],
    ['  Αγίου   Δημητρίου  ', 'αγιου δημητριου'],
    ['Πρωτεΐνη', 'πρωτεινη'],
    ['αϋπνία', 'αυπνια'],
    ['ᾠδή', 'ωδη'],
    ['Ἀθῆναι', 'αθηναι'],
    ['Ζωή\u00a0Νικολάου', 'ζωη νικολαου'],
    ['Μάιος', 'μαιοσ'],
    ['Café Ödön', 'cafe odon'],
    ['ΑΣΦ-2026-004471', 'ασφ-2026-004471'],
    ['', ''],
    ['   ', ''],
  ])('%s → %s', (input, expected) => {
    expect(normalizeForSearch(input)).toBe(expected);
  });

  it('works on already-decomposed (NFD) input', () => {
    expect(normalizeForSearch('Γεώργιος'.normalize('NFD'))).toBe('γεωργιοσ');
  });
});

describe('greekToGreeklish (ELOT 743)', () => {
  it.each([
    ['Γεώργιος Παπαδόπουλος', 'Georgios Papadopoulos'],
    ['Θεσσαλονίκη', 'Thessaloniki'],
    ['ΘΕΣΣΑΛΟΝΙΚΗ', 'THESSALONIKI'],
    ['Αθήνα', 'Athina'],
    ['Πειραιάς', 'Peiraias'],
    ['Χανιά', 'Chania'],
    ['Ψυχικό', 'Psychiko'],
    ['Ξάνθη', 'Xanthi'],
    ['Φωκίδα', 'Fokida'],
    ['Βόλος', 'Volos'],
    ['Ζωή', 'Zoi'],
    ['Ηράκλειο', 'Irakleio'],
    ['Ύδρα', 'Ydra'],
    ['Ωρωπός', 'Oropos'],
    // αυ / ευ / ηυ
    ['Ευάγγελος', 'Evangelos'],
    ['Αύριο', 'Avrio'],
    ['Αυγή', 'Avgi'],
    ['Ευλογία', 'Evlogia'],
    ['αυτοκίνητο', 'aftokinito'],
    ['Ευθύμιος', 'Efthymios'],
    ['Ευτυχία', 'Eftychia'],
    ['Ζευς', 'Zefs'],
    ['ηύρα', 'ivra'],
    ['άυλος', 'aylos'],
    ['αϋπνία', 'aypnia'],
    // ου
    ['Πουλόπουλος', 'Poulopoulos'],
    ['ΟΥΡΑΝΟΣ', 'OURANOS'],
    ['Ουρανία', 'Ourania'],
    // μπ / ντ
    ['Μπάμπης', 'Bampis'],
    ['ΜΠΑΜΠΗΣ', 'BAMPIS'],
    ['Ντίνος', 'Dinos'],
    ['Αντώνης', 'Antonis'],
    ['κοντά', 'konta'],
    // γγ / γκ / γξ / γχ
    ['Άγγελος', 'Angelos'],
    ['Γκίκας', 'Gikas'],
    ['Άγκυρα', 'Ankyra'],
    ['σφίγξ', 'sfinx'],
    ['Μελαγχολία', 'Melancholia'],
    // mixed and non-Greek text
    ['Οδός Ερμού 12, 105 63 Αθήνα', 'Odos Ermou 12, 105 63 Athina'],
    ['AXA Ασφαλιστική', 'AXA Asfalistiki'],
    ['Γ. Παπαδόπουλος', 'G. Papadopoulos'],
    ['ΑΣΦ-2026-004471', 'ASF-2026-004471'],
    ['', ''],
  ])('%s → %s', (input, expected) => {
    expect(greekToGreeklish(input)).toBe(expected);
  });
});

describe('greeklishToGreek', () => {
  it.each([
    ['Papadopoulos', 'Παπαδοπουλος'],
    ['Thessaloniki', 'Θεσσαλονικι'],
    ['8essaloniki', 'θεσσαλονικι'],
    ['Evangelos', 'Ευαγγελος'],
    ['aftokinito', 'αυτοκινιτο'],
    ['aftokinhto', 'αυτοκινητο'],
    ['Afroditi', 'Αφροδιτι'],
    ['kafes', 'καφες'],
    ['psychi', 'ψυχι'],
    ['ksenia', 'ξενια'],
    ['xenia', 'ξενια'],
    ['Mpampis', 'Μπαμπις'],
    ['Bampis', 'Μπαμπις'],
    ['Antonis', 'Αντονις'],
    ['Gkikas', 'Γκικας'],
    ['nero', 'νερο'],
    ['kalwς', 'καλως'],
    ['Zwh', 'Ζωη'],
    ['ATHINA', 'ΑΘΙΝΑ'],
    ['odos 12', 'οδος 12'],
  ])('%s → %s', (input, expected) => {
    expect(greeklishToGreek(input)).toBe(expected);
  });

  it('keeps numbers and punctuation', () => {
    expect(greeklishToGreek('Ermou 12, 105 63')).toBe('Ερμου 12, 105 63');
  });
});

describe('matchesQuery', () => {
  it('matches an empty query with no ranges', () => {
    expect(matchesQuery('Παπαδόπουλος', '   ')).toEqual({ ranges: [], wordPrefix: true, score: 0 });
  });

  it.each([
    // Greek, accent- and case-insensitive
    ['Γεώργιος Παπαδόπουλος', 'παπαδ', ['Παπαδ']],
    ['Γεώργιος Παπαδόπουλος', 'ΠΑΠΑΔΟ', ['Παπαδό']],
    ['Γεώργιος Παπαδόπουλος', 'γεωργ', ['Γεώργ']],
    ['Γεώργιος Παπαδόπουλος', 'γεώργιος', ['Γεώργιος']],
    ['Οδυσσέας Ελύτης', 'ελυτησ', ['Ελύτης']],
    ['Οδυσσέας Ελύτης', 'ελυτης', ['Ελύτης']],
    // Latin ↔ Greek (ELOT 743)
    ['Γεώργιος Παπαδόπουλος', 'papad', ['Παπαδ']],
    ['Γεώργιος Παπαδόπουλος', 'Papadopoulos', ['Παπαδόπουλος']],
    ['Γεώργιος Παπαδόπουλος', 'georgios', ['Γεώργιος']],
    ['Θεσσαλονίκη', 'thess', ['Θεσσ']],
    ['Ευάγγελος Βενιζέλος', 'evangelos', ['Ευάγγελος']],
    ['Ευθύμιος', 'efth', ['Ευθ']],
    ['Μπάμπης Ντίνος', 'bampis', ['Μπάμπης']],
    ['Μπάμπης Ντίνος', 'dinos', ['Ντίνος']],
    ['Άγγελος', 'angelos', ['Άγγελος']],
    ['Ψυχικό', 'psychiko', ['Ψυχικό']],
    // informal Greeklish
    ['Θεσσαλονίκη', '8essaloniki', ['Θεσσαλονίκη']],
    ['Ζωή Χατζή', 'zwh', ['Ζωή']],
    ['Ζωή Χατζή', 'hatzi', ['Χατζή']],
    ['Ζωή Χατζή', 'xatzh', ['Χατζή']],
    ['Ξενία', 'ksenia', ['Ξενία']],
    ['Ξενία', 'xenia', ['Ξενία']],
    ['Μπάμπης', 'mpampis', ['Μπάμπης']],
    ['Γκίκας', 'gkikas', ['Γκίκας']],
    ['Γκίκας', 'gikas', ['Γκίκας']],
    ['Ουρανία', 'urania', ['Ουρανία']],
    ['Ειρήνη', 'irini', ['Ειρήνη']],
    ['Ειρήνη', 'eirini', ['Ειρήνη']],
    // a partial final token («kat» already matches «Καθ»)
    ['Καθηγητής', 'kat', ['Καθ']],
    // Greek query → Latin candidate
    ['Papadopoulos Georgios', 'παπαδ', ['Papad']],
    ['AXA Insurance', 'αξα', ['AXA']],
    ['Thessaloniki Branch', 'θεσσ', ['Thess']],
    // identifiers and digits
    ['ΑΣΦ-2026-004471', '4471', ['4471']],
    ['ΑΣΦ-2026-004471', '2026', ['2026']],
    ['ΑΣΦ-2026-004471', '2026004471', ['2026-004471']],
    ['ΑΣΦ-2026-004471', 'ασφ', ['ΑΣΦ']],
    ['ΑΣΦ-2026-004471', 'asf', ['ΑΣΦ']],
    // several words, any order
    ['Γεώργιος Παπαδόπουλος', 'παπ γεω', ['Γεώ', 'Παπ']],
    ['Γεώργιος Παπαδόπουλος', 'pap georg', ['Γεώργ', 'Παπ']],
  ])('%s ~ %s', (candidate, query, expected) => {
    expect(highlighted(candidate, query)).toEqual(expected);
  });

  it.each([
    ['Γεώργιος Παπαδόπουλος', 'παπαδακης'],
    ['Γεώργιος Παπαδόπουλος', 'xyz'],
    ['Γεώργιος Παπαδόπουλος', 'παπ νικ'],
    ['ΑΣΦ-2026-004471', '4472'],
    // a single character only matches at the start of a word
    ['Γεώργιος Παπαδόπουλος', 'ω'],
    ['Γεώργιος Παπαδόπουλος', 'r'],
  ])('%s does not match %s', (candidate, query) => {
    expect(matchesQuery(candidate, query)).toBeNull();
  });

  it('matches a single character at the start of a word, in either script', () => {
    expect(highlighted('Γεώργιος Παπαδόπουλος', 'π')).toEqual(['Π']);
    expect(highlighted('Γεώργιος Παπαδόπουλος', 'p')).toEqual(['Π']);
    expect(highlighted('Θεσσαλονίκη', 't')).toEqual(['Θ']);
  });

  it('finds matches inside words for two or more characters', () => {
    expect(highlighted('Γεώργιος Παπαδόπουλος', 'πουλ')).toEqual(['πουλ']);
    expect(highlighted('Γεώργιος Παπαδόπουλος', 'poul')).toEqual(['πουλ']);
    expect(matchesQuery('Γεώργιος Παπαδόπουλος', 'πουλ')?.wordPrefix).toBe(false);
  });

  it('scores word-start and direct matches higher', () => {
    const prefix = matchesQuery('Παπαδόπουλος', 'παπ');
    const inside = matchesQuery('Παπαδόπουλος', 'πουλ');
    const loose = matchesQuery('Παπαδόπουλος', 'pap');
    expect(prefix?.wordPrefix).toBe(true);
    expect(prefix?.score).toBeGreaterThan(inside?.score ?? 0);
    expect(prefix?.score).toBeGreaterThan(loose?.score ?? 0);
  });

  it('maps ranges onto decomposed and spaced originals', () => {
    const nfd = 'Γεώργιος   Παπαδόπουλος'.normalize('NFD');
    const m = matchesQuery(nfd, 'παπα');
    expect(m).not.toBeNull();
    const r = m?.ranges[0];
    expect(r && nfd.slice(r.start, r.end)).toBe('Παπα');
  });
});

describe('createGreekFilter', () => {
  it('has the React Aria filter signature', () => {
    const filter = createGreekFilter();
    expect(filter('Γεώργιος Παπαδόπουλος', 'papad')).toBe(true);
    expect(filter('Γεώργιος Παπαδόπουλος', 'Παπαδ')).toBe(true);
    expect(filter('Γεώργιος Παπαδόπουλος', 'Νικ')).toBe(false);
    expect(filter('ΑΣΦ-2026-004471', '4471')).toBe(true);
    expect(filter('anything', '')).toBe(true);
  });
});

describe('searchItems', () => {
  const people = [
    { id: 1, name: 'Νίκος Παπαδάκης', town: 'Ηράκλειο' },
    { id: 2, name: 'Γεώργιος Παπαδόπουλος', town: 'Αθήνα' },
    { id: 3, name: 'Μαρία Αποστόλου', town: 'Πάτρα' },
  ];

  it('filters and ranks word-start matches first', () => {
    const hits = searchItems(people, 'pap', (p) => [p.name, p.town]);
    expect(hits.map((h) => h.item.id)).toEqual([1, 2]);
  });

  it('searches secondary texts when the first does not match', () => {
    const hits = searchItems(people, 'patra', (p) => [p.name, p.town]);
    expect(hits.map((h) => h.item.id)).toEqual([3]);
  });

  it('returns everything for an empty query', () => {
    expect(searchItems(people, '', (p) => p.name)).toHaveLength(3);
  });
});
