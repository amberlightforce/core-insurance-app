import { describe, expect, it } from 'vitest';

import {
  applyFinalSigma,
  compareGreek,
  hasGreek,
  stripGreekDiacritics,
  toGreekLower,
  toGreekUpper,
} from './greek';

describe('toGreekUpper', () => {
  it.each([
    // Mandatory cases (Part 4 §11.8).
    ['Μάιος', 'ΜΑΪΟΣ'],
    ['ρολόι', 'ΡΟΛΟΪ'],
    ['πρωτεΐνη', 'ΠΡΩΤΕΪΝΗ'],
    ['αϋπνία', 'ΑΫΠΝΙΑ'],
    ['ευρώ', 'ΕΥΡΩ'],
    ['Ασφαλιστήριο', 'ΑΣΦΑΛΙΣΤΗΡΙΟ'],
    ['ᾠδή', 'ΩΙΔΗ'],
    ['άυλος', 'ΑΫΛΟΣ'],
    // Negative cases: a diphthong with the accent on its second vowel keeps no dialytika.
    ['παίζω', 'ΠΑΙΖΩ'],
    ['αύριο', 'ΑΥΡΙΟ'],
    // More everyday cases.
    ['Αυτοκίνητο ΙΧ', 'ΑΥΤΟΚΙΝΗΤΟ ΙΧ'],
    ['Μετάβαση σε', 'ΜΕΤΑΒΑΣΗ ΣΕ'],
    ['παραπομπή', 'ΠΑΡΑΠΟΜΠΗ'],
    ['Υλικές ζημιές', 'ΥΛΙΚΕΣ ΖΗΜΙΕΣ'],
    ['οδός', 'ΟΔΟΣ'],
    ['κορόιδο', 'ΚΟΡΟΪΔΟ'],
    ['τσάι', 'ΤΣΑΪ'],
    ['νεράιδα', 'ΝΕΡΑΪΔΑ'],
    ['Κάιρο', 'ΚΑΪΡΟ'],
    ['θεϊκός', 'ΘΕΪΚΟΣ'],
    ['Ελλάδα', 'ΕΛΛΑΔΑ'],
    ['ήλιος', 'ΗΛΙΟΣ'],
    ['Ἀθῆναι', 'ΑΘΗΝΑΙ'],
    ['τῇ', 'ΤΗΙ'],
    ['ΠΕΡΙΒΑΛΛΟΝ ΔΟΚΙΜΩΝ', 'ΠΕΡΙΒΑΛΛΟΝ ΔΟΚΙΜΩΝ'],
    ['UAT · Τα δεδομένα', 'UAT · ΤΑ ΔΕΔΟΜΕΝΑ'],
    ['policy ΑΣΦ-2026-004471', 'POLICY ΑΣΦ-2026-004471'],
    ['', ''],
  ])('%s → %s', (input, expected) => {
    expect(toGreekUpper(input)).toBe(expected);
  });

  it('never leaves a tonos in its output', () => {
    const sample = 'Ζημία ά έ ή ί ό ύ ώ ΐ ΰ Ά Έ Ή Ί Ό Ύ Ώ';
    expect(toGreekUpper(sample)).not.toMatch(/[ΆΈΉΊΌΎΏάέήίόύώ]/);
  });
});

describe('final sigma', () => {
  it.each([
    ['ΟΔΟΣ', 'οδος'.replace(/σ$/, 'ς')],
    ['ΣΟΦΙΑ ΣΤΗΝ ΑΘΗΝΑ', 'σοφια στην αθηνα'],
    ['ΚΩΣΤΑΣ, ΜΑΡΙΑ', 'κωστας, μαρια'],
    ['ΑΣΦ-2026', 'ασφ-2026'],
  ])('toGreekLower(%s) → %s', (input, expected) => {
    expect(toGreekLower(input)).toBe(expected);
  });

  it('fixes misplaced sigmas both ways', () => {
    expect(applyFinalSigma('κοσμοσ ςτο')).toBe('κοσμος στο');
    expect(applyFinalSigma('σ')).toBe('σ');
  });
});

describe('helpers', () => {
  it('strips diacritics', () => {
    expect(stripGreekDiacritics('Παπαδόπουλος Ϊ ΐ')).toBe('Παπαδοπουλος Ι ι');
  });

  it('detects Greek', () => {
    expect(hasGreek('abc')).toBe(false);
    expect(hasGreek('abγ')).toBe(true);
    expect(hasGreek('ἀ')).toBe(true);
  });

  it('collates accent-insensitively and numerically', () => {
    expect(compareGreek('ά', 'α')).toBe(0);
    expect(['ΑΣΦ-10', 'ΑΣΦ-2'].sort(compareGreek)).toEqual(['ΑΣΦ-2', 'ΑΣΦ-10']);
  });
});
