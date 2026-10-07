import { describe, expect, it } from 'vitest';

import {
  amountInWords,
  integerToEnglishWords,
  integerToGreekWords,
  toMinorUnits,
} from './amount-in-words';

describe('amountInWords (Greek, EUR)', () => {
  it.each([
    ['1234.56', 'χίλια διακόσια τριάντα τέσσερα ευρώ και πενήντα έξι λεπτά'],
    ['12480', 'δώδεκα χιλιάδες τετρακόσια ογδόντα ευρώ'],
    ['0', 'μηδέν ευρώ'],
    ['1', 'ένα ευρώ'],
    ['0.01', 'ένα λεπτό'],
    ['0.50', 'πενήντα λεπτά'],
    ['1.01', 'ένα ευρώ και ένα λεπτό'],
    ['3', 'τρία ευρώ'],
    ['4.04', 'τέσσερα ευρώ και τέσσερα λεπτά'],
    ['13', 'δεκατρία ευρώ'],
    ['14', 'δεκατέσσερα ευρώ'],
    ['11', 'έντεκα ευρώ'],
    ['21', 'είκοσι ένα ευρώ'],
    ['100', 'εκατό ευρώ'],
    ['101', 'εκατόν ένα ευρώ'],
    ['115', 'εκατόν δεκαπέντε ευρώ'],
    ['200', 'διακόσια ευρώ'],
    ['999', 'εννιακόσια ενενήντα εννέα ευρώ'],
    ['1000', 'χίλια ευρώ'],
    ['1001', 'χίλια ένα ευρώ'],
    ['2000', 'δύο χιλιάδες ευρώ'],
    ['3000', 'τρεις χιλιάδες ευρώ'],
    ['4004', 'τέσσερις χιλιάδες τέσσερα ευρώ'],
    ['13000', 'δεκατρείς χιλιάδες ευρώ'],
    ['14000', 'δεκατέσσερις χιλιάδες ευρώ'],
    ['21000', 'είκοσι μία χιλιάδες ευρώ'],
    ['100000', 'εκατό χιλιάδες ευρώ'],
    ['101000', 'εκατόν μία χιλιάδες ευρώ'],
    ['200000', 'διακόσιες χιλιάδες ευρώ'],
    ['234567', 'διακόσιες τριάντα τέσσερις χιλιάδες πεντακόσια εξήντα επτά ευρώ'],
    ['1000000', 'ένα εκατομμύριο ευρώ'],
    ['1500000', 'ένα εκατομμύριο πεντακόσιες χιλιάδες ευρώ'],
    ['3000000', 'τρία εκατομμύρια ευρώ'],
    ['204000000', 'διακόσια τέσσερα εκατομμύρια ευρώ'],
    ['1000000000', 'ένα δισεκατομμύριο ευρώ'],
    [
      '999999999.99',
      'εννιακόσια ενενήντα εννέα εκατομμύρια εννιακόσιες ενενήντα εννέα χιλιάδες εννιακόσια ενενήντα εννέα ευρώ και ενενήντα εννέα λεπτά',
    ],
    ['-128.40', 'μείον εκατόν είκοσι οκτώ ευρώ και σαράντα λεπτά'],
    ['−128.40', 'μείον εκατόν είκοσι οκτώ ευρώ και σαράντα λεπτά'],
  ])('%s → %s', (input, expected) => {
    expect(amountInWords(input)).toBe(expected);
  });

  it('accepts exact numbers but rejects float noise instead of rounding it', () => {
    expect(amountInWords(6480)).toBe('έξι χιλιάδες τετρακόσια ογδόντα ευρώ');
    expect(amountInWords(19.99)).toBe('δεκαεννέα ευρώ και ενενήντα εννέα λεπτά');
    expect(() => amountInWords(0.1 + 0.2)).toThrow(RangeError);
    expect(() => amountInWords(1.005)).toThrow(RangeError);
    expect(() => amountInWords(Number.NaN)).toThrow(RangeError);
    expect(() => amountInWords(1e21)).toThrow(RangeError);
  });

  it('capitalises on request', () => {
    expect(amountInWords('12480', { capitalize: true })).toBe(
      'Δώδεκα χιλιάδες τετρακόσια ογδόντα ευρώ',
    );
  });

  it('agrees with a feminine currency', () => {
    expect(amountInWords('1', { currency: 'GBP' })).toBe('μία λίρα');
    expect(amountInWords('3.04', { currency: 'GBP' })).toBe('τρεις λίρες και τέσσερις πένες');
    expect(amountInWords('1300', { currency: 'GBP' })).toBe('χίλιες τριακόσιες λίρες');
    expect(amountInWords('1000000', { currency: 'GBP' })).toBe('ένα εκατομμύριο λίρες');
    expect(amountInWords('234', { currency: 'GBP' })).toBe('διακόσιες τριάντα τέσσερις λίρες');
  });

  it('rejects extra decimals instead of rounding', () => {
    expect(() => amountInWords('1.234')).toThrow(RangeError);
    expect(amountInWords('1.230')).toBe('ένα ευρώ και είκοσι τρία λεπτά');
  });

  it('rejects junk', () => {
    expect(() => amountInWords('12a')).toThrow(RangeError);
    expect(() => amountInWords('1', { currency: 'XXX' })).toThrow(RangeError);
  });
});

describe('amountInWords (English)', () => {
  it.each([
    ['1234.56', 'one thousand two hundred and thirty-four euros and fifty-six cents'],
    ['1', 'one euro'],
    ['0.01', 'one cent'],
    ['2000000', 'two million euros'],
    ['-5', 'minus five euros'],
  ])('%s → %s', (input, expected) => {
    expect(amountInWords(input, { language: 'en' })).toBe(expected);
  });
});

describe('integer helpers', () => {
  it('gives masculine forms', () => {
    expect(integerToGreekWords(1n, 'masculine')).toBe('ένας');
    expect(integerToGreekWords(304n, 'masculine')).toBe('τριακόσιοι τέσσερις');
  });

  it('handles trillions and rejects out of range', () => {
    expect(integerToGreekWords(2_000_000_000_000n)).toBe('δύο τρισεκατομμύρια');
    expect(() => integerToGreekWords(10n ** 15n)).toThrow(RangeError);
    expect(integerToEnglishWords(1_000_001n)).toBe('one million one');
  });

  it('converts to minor units exactly', () => {
    expect(toMinorUnits('1234.5', 2)).toBe(123450n);
    expect(toMinorUnits('-0.07', 2)).toBe(-7n);
    expect(toMinorUnits(19.99, 2)).toBe(1999n);
  });
});
