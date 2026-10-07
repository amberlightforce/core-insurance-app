import { describe, expect, it } from 'vitest';

import {
  formatCompact,
  formatInteger,
  formatMoney,
  formatNumber,
  formatPercent,
  formatPercentagePoints,
  formatPerMille,
  MINUS_SIGN,
  NARROW_NBSP,
  NBSP,
} from './numbers';

describe('formatMoney', () => {
  it.each([
    [1234.56, `1.234,56${NBSP}€`],
    ['1234.56', `1.234,56${NBSP}€`],
    [0, `0,00${NBSP}€`],
    [412.38, `412,38${NBSP}€`],
    ['999999999.99', `999.999.999,99${NBSP}€`],
    ['1234567890123.45', `1.234.567.890.123,45${NBSP}€`],
    [-128.4, `${MINUS_SIGN}128,40${NBSP}€`],
    ['-1234.56', `${MINUS_SIGN}1.234,56${NBSP}€`],
  ])('el-GR %s → %s', (value, expected) => {
    expect(formatMoney(value)).toBe(expected);
  });

  it('formats en-GB with the symbol first and a true minus', () => {
    expect(formatMoney(1234.56, { region: 'en-GB' })).toBe('€1,234.56');
    expect(formatMoney(-1234.56, { region: 'en-GB' })).toBe(`${MINUS_SIGN}€1,234.56`);
  });

  it('shows the sign for deltas', () => {
    expect(formatMoney(1260, { signDisplay: 'always' })).toBe(`+1.260,00${NBSP}€`);
  });

  it('can omit the currency for cells with the unit elsewhere', () => {
    expect(formatMoney('5220', { showCurrency: false })).toBe('5.220,00');
  });

  it('keeps exact decimal strings (no float rounding)', () => {
    expect(formatMoney('0.10')).toBe(`0,10${NBSP}€`);
    expect(formatMoney('90071992547409.93')).toBe(`90.071.992.547.409,93${NBSP}€`);
  });

  it('uses currency precision', () => {
    expect(formatMoney(1234, { currency: 'JPY' })).toMatch(/^1\.234/);
  });

  it('never shows negative zero', () => {
    expect(formatMoney(-0)).toBe(`0,00${NBSP}€`);
    expect(formatMoney('-0.00')).toBe(`0,00${NBSP}€`);
    expect(formatMoney('-0.004')).toBe(`0,00${NBSP}€`);
    expect(formatMoney(-0.001, { region: 'en-GB' })).toBe('€0.00');
    expect(formatNumber(-0)).toBe('0');
    expect(formatNumber('-0.001')).toBe('0');
    expect(formatMoney('-0.01')).toBe(`${MINUS_SIGN}0,01${NBSP}€`);
  });

  it('rejects non-numeric strings', () => {
    expect(() => formatMoney('12,50')).toThrow(RangeError);
  });
});

describe('formatNumber and integers', () => {
  it('groups with dots and uses a comma decimal', () => {
    expect(formatNumber(1234567.89)).toBe('1.234.567,89');
    expect(formatNumber(1234567.89, { region: 'en-GB' })).toBe('1,234,567.89');
    expect(formatNumber(-5.5)).toBe(`${MINUS_SIGN}5,5`);
    expect(formatInteger(4812)).toBe('4.812');
    expect(formatInteger(1234)).toBe('1.234');
  });
});

describe('percent and per mille', () => {
  it('puts a narrow no-break space before % in Greek', () => {
    expect(formatPercent(15, { fractionDigits: 0 })).toBe(`15${NARROW_NBSP}%`);
    expect(formatPercent(7.5)).toBe(`7,50${NARROW_NBSP}%`);
    expect(formatPercent(-7.5, { signDisplay: 'always' })).toBe(`${MINUS_SIGN}7,50${NARROW_NBSP}%`);
    expect(formatPercent(15, { region: 'en-GB', fractionDigits: 0 })).toBe('15%');
    expect(formatPercent('12.3456', { fractionDigits: 4 })).toBe(`12,3456${NARROW_NBSP}%`);
  });

  it('formats per mille', () => {
    expect(formatPerMille(2.5)).toBe(`2,5${NARROW_NBSP}‰`);
    expect(formatPerMille(2.5, { region: 'en-GB' })).toBe('2.5‰');
  });

  it('formats percentage points', () => {
    expect(formatPercentagePoints(1.5)).toBe(`+1,50${NBSP}μ.`);
    expect(formatPercentagePoints(-0.25, { region: 'en-GB' })).toBe(`${MINUS_SIGN}0.25${NBSP}pp`);
  });
});

describe('formatCompact (χιλ. / εκ. / δισ.)', () => {
  it.each([
    [1234, `1,23${NBSP}χιλ.`],
    [1284390, `1,28${NBSP}εκ.`],
    [12345678901, `12,35${NBSP}δισ.`],
    [999, '999'],
    [-45000, `${MINUS_SIGN}45${NBSP}χιλ.`],
  ])('%s → %s', (value, expected) => {
    expect(formatCompact(value)).toBe(expected);
  });

  it('adds the currency', () => {
    expect(formatCompact(1284390, { currency: 'EUR' })).toBe(`1,28${NBSP}εκ.${NBSP}€`);
    expect(formatCompact(1284390, { currency: 'EUR', region: 'en-GB' })).toBe('€1.28M');
  });
});
