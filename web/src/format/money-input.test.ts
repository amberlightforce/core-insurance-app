import { describe, expect, it } from 'vitest';

import {
  compareMoney,
  formatLive,
  formatMoneyDisplay,
  formatMoneyGrouping,
  formatMoneyInputText,
  fromMinor,
  parseMoneyInput,
  percentOfAmount,
  toMinor,
  typedFractionDigits,
} from './money-input';

const NBSP = '\u00a0';

describe('parseMoneyInput (el-GR)', () => {
  it.each([
    // plain and grouped integers
    ['0', '0.00'],
    ['7', '7.00'],
    ['1234', '1234.00'],
    ['007', '7.00'],
    ['1.234', '1234.00'],
    ['1.234.567', '1234567.00'],
    ['999.999.999', '999999999.00'],
    // rule 1: «,» + 1–2 digits is a decimal
    ['1234,5', '1234.50'],
    ['1234,56', '1234.56'],
    ['1.234,56', '1234.56'],
    ['999.999.999,99', '999999999.99'],
    [',5', '0.50'],
    ['0,05', '0.05'],
    ['12,', '12.00'],
    // decorations
    ['1.234,56 €', '1234.56'],
    ['€ 1.234,56', '1234.56'],
    [`1.234,56${NBSP}€`, '1234.56'],
    ['  12 345,6 ', '12345.60'],
    ['1234,56 ευρώ', '1234.56'],
    ['+12', '12.00'],
    // shorthand
    ['12k', '12000.00'],
    ['12K', '12000.00'],
    ['12χ', '12000.00'],
    ['12 χιλ.', '12000.00'],
    ['1.5m', '1500000.00'],
    ['1,5m', '1500000.00'],
    ['1,5ε', '1500000.00'],
    ['1,5εκ', '1500000.00'],
    ['2,25k', '2250.00'],
    ['1,2345k', '1234.50'],
    ['0,5k', '500.00'],
  ])('%s → %s', (text, value) => {
    expect(parseMoneyInput(text)).toEqual({ ok: true, value });
  });

  it.each([
    // rule 3: a single «.» + 1–2 digits is read as the decimal separator (with a hint)
    ['1234.56', '1234.56'],
    ['1234.5', '1234.50'],
    ['12.5', '12.50'],
    ['0.99', '0.99'],
  ])('%s → %s (interpreted)', (text, value) => {
    expect(parseMoneyInput(text)).toEqual({ ok: true, value, interpreted: true });
  });

  it.each([['1234,567'], ['0,001'], ['1.234,5678'], ['1,23456789k']])(
    'rejects extra decimals in %s (never rounds)',
    (text) => {
      expect(parseMoneyInput(text)).toEqual({ ok: false, error: 'tooManyDecimals' });
    },
  );

  it.each([
    [''],
    ['   '],
    ['abc'],
    ['12a'],
    ['1,2,3'],
    ['1.23.4'],
    ['1.2345'],
    ['0.123'],
    ['12.'],
    ['1,234.56'],
    ['12..5'],
    [','],
    ['12q'],
    ['1.5.6k'],
    ['--5'],
    ['12345678901234'],
  ])('rejects %s as invalid', (text) => {
    expect(parseMoneyInput(text)).toEqual({ ok: false, error: 'invalid' });
  });

  it('honours the currency precision', () => {
    expect(parseMoneyInput('1234', { maxFractionDigits: 0 })).toEqual({ ok: true, value: '1234' });
    expect(parseMoneyInput('1234,5', { maxFractionDigits: 0 })).toEqual({
      ok: false,
      error: 'tooManyDecimals',
    });
    expect(parseMoneyInput('1,234', { maxFractionDigits: 3 })).toEqual({
      ok: true,
      value: '1.234',
    });
  });

  describe('negatives', () => {
    it.each([['-12,5'], ['\u221212,5'], ['12,5-'], ['\u22121.234,56 €']])(
      'rejects %s unless allowed',
      (text) => {
        expect(parseMoneyInput(text)).toEqual({ ok: false, error: 'negativeNotAllowed' });
      },
    );

    it.each([
      ['-12,5', '-12.50'],
      ['\u221212,5', '-12.50'],
      ['12,5\u2212', '-12.50'],
      ['\u22121.234,56 €', '-1234.56'],
      ['-1,5k', '-1500.00'],
      ['-0', '0.00'],
    ])('%s → %s when allowed', (text, value) => {
      expect(parseMoneyInput(text, { allowNegative: true })).toEqual({ ok: true, value });
    });

    it('treats a negative zero as zero even when negatives are not allowed', () => {
      expect(parseMoneyInput('-0,00')).toEqual({ ok: true, value: '0.00' });
    });
  });
});

describe('parseMoneyInput (en-GB)', () => {
  const en = { locale: 'en-GB' as const };
  it.each([
    ['1234.56', '1234.56'],
    ['1,234.56', '1234.56'],
    ['1,234', '1234.00'],
    ['1,234,567.8', '1234567.80'],
    ['€1,234.56', '1234.56'],
    ['12k', '12000.00'],
    ['1.5m', '1500000.00'],
  ])('%s → %s', (text, value) => {
    expect(parseMoneyInput(text, en)).toEqual({ ok: true, value });
  });

  it('reads a single «,» + 1\u20132 digits as the decimal separator (interpreted)', () => {
    expect(parseMoneyInput('1234,56', en)).toEqual({
      ok: true,
      value: '1234.56',
      interpreted: true,
    });
  });

  it('rejects extra decimals and invalid text', () => {
    expect(parseMoneyInput('1.234', en)).toEqual({ ok: false, error: 'tooManyDecimals' });
    expect(parseMoneyInput('1.234,56', en)).toEqual({ ok: false, error: 'invalid' });
  });
});

describe('typedFractionDigits', () => {
  it('counts digits after the decimal separator', () => {
    expect(typedFractionDigits('1.234,567')).toBe(3);
    expect(typedFractionDigits('1.234')).toBe(0);
    expect(typedFractionDigits('1,234.5', 'en-GB')).toBe(1);
  });
});

describe('formatLive', () => {
  it.each([
    // [raw, caret, text, caret]
    ['1234', 4, '1.234', 5],
    ['12345', 5, '12.345', 6],
    ['1234567', 7, '1.234.567', 9],
    ['1.2345', 6, '12.345', 6],
    ['12.345,6', 8, '12.345,6', 8],
    ['1234,56', 7, '1.234,56', 8],
    // caret in the middle stays on the same digit
    ['12534', 3, '12.534', 4],
    ['1.2534', 4, '12.534', 4],
    // deleting a digit regroups
    ['1.23', 4, '123', 3],
    ['12.34', 1, '1.234', 1],
    // the numpad «.» typed as a decimal separator
    ['1234.', 5, '1.234,', 6],
    ['12.', 3, '12,', 3],
    // a second decimal separator is dropped
    ['12,3,4', 6, '12,34', 5],
    // sign and shorthand
    ['-1234', 5, '\u22121.234', 6],
    ['12k', 3, '12k', 3],
    ['1,5ε', 4, '1,5ε', 4],
    // junk is dropped
    ['12a34', 5, '1.234', 5],
    ['', 0, '', 0],
  ])('%s (caret %i) → %s (caret %i)', (raw, caret, text, newCaret) => {
    expect(formatLive(raw, caret)).toEqual({ text, caret: newCaret });
  });

  it('does not turn a «.» into a decimal when a «,» already exists', () => {
    expect(formatLive('1,5.', 4).text).toBe('1,5');
  });

  it('groups with «,» in en-GB', () => {
    expect(formatLive('1234567.8', 9, { locale: 'en-GB' })).toEqual({
      text: '1,234,567.8',
      caret: 11,
    });
    expect(formatLive('1234,', 5, { locale: 'en-GB' })).toEqual({ text: '1,234.', caret: 6 });
  });
});

describe('formatMoneyGrouping', () => {
  it('groups typed text', () => {
    expect(formatMoneyGrouping('1234567,8')).toBe('1.234.567,8');
    expect(formatMoneyGrouping('1234567.8', 'en-GB')).toBe('1,234,567.8');
  });
});

describe('formatMoneyDisplay / formatMoneyInputText', () => {
  it('formats el-GR with NBSP before € and U+2212', () => {
    expect(formatMoneyDisplay('1234.56')).toBe(`1.234,56${NBSP}€`);
    expect(formatMoneyDisplay('-1234.5')).toBe(`\u22121.234,50${NBSP}€`);
    expect(formatMoneyDisplay('999999999.99')).toBe(`999.999.999,99${NBSP}€`);
  });

  it('formats en-GB with a leading €', () => {
    expect(formatMoneyDisplay('1234.56', 'en-GB')).toBe('€1,234.56');
  });

  it('formats the input text without the currency', () => {
    expect(formatMoneyInputText('1234.5')).toBe('1.234,50');
    expect(formatMoneyInputText('-12')).toBe('\u221212,00');
    expect(formatMoneyInputText('1234.5', 'en-GB')).toBe('1,234.50');
  });

  it('keeps precision beyond floating point', () => {
    expect(formatMoneyDisplay('9007199254740993.01')).toBe(`9.007.199.254.740.993,01${NBSP}€`);
  });
});

describe('minor units and comparisons', () => {
  it('converts exactly', () => {
    expect(toMinor('1234.5')).toBe(123450n);
    expect(toMinor('-0.01')).toBe(-1n);
    expect(toMinor('12')).toBe(1200n);
    expect(fromMinor(123450n)).toBe('1234.50');
    expect(fromMinor(-1n)).toBe('-0.01');
    expect(fromMinor(5n, 0)).toBe('5');
    expect(() => toMinor('1.234')).toThrow(RangeError);
    expect(() => toMinor('abc')).toThrow(RangeError);
  });

  it('compares decimal strings', () => {
    expect(compareMoney('5000.00', '5000')).toBe(0);
    expect(compareMoney('5000.01', '5000')).toBe(1);
    expect(compareMoney('-1', '0')).toBe(-1);
  });

  it('computes percentages of amounts with half-away-from-zero rounding', () => {
    expect(percentOfAmount('416.00', '-7.5')).toBe('-31.20');
    expect(percentOfAmount('100.00', 15)).toBe('15.00');
    expect(percentOfAmount('0.10', '5')).toBe('0.01');
    expect(percentOfAmount('0.10', '-5')).toBe('-0.01');
    expect(percentOfAmount('412.38', '2.75')).toBe('11.34');
  });
});
