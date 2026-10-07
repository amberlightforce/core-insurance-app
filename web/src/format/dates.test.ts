import { describe, expect, it } from 'vitest';

import {
  athensZoneAbbreviation,
  formatDate,
  formatDateLong,
  formatDateTime,
  formatDayMonth,
  formatMonthYear,
  formatRelative,
  formatTime,
  toDate,
} from './dates';

const instant = '2026-10-07T11:32:00Z'; // 14:32 in Athens (EEST)

describe('dates', () => {
  it('formats the short date as dd/MM/yyyy', () => {
    expect(formatDate(instant)).toBe('07/10/2026');
    expect(formatDate('2026-01-15')).toBe('15/01/2026');
    expect(formatDate({ year: 2027, month: 5, day: 2 })).toBe('02/05/2027');
    expect(formatDate(instant, 'en-GB')).toBe('07/10/2026');
  });

  it('uses Athens time, 24-hour', () => {
    expect(formatTime(instant)).toBe('14:32');
    expect(formatTime('2026-01-15T22:05:00Z')).toBe('00:05');
    expect(formatDateTime(instant)).toBe('07/10/2026 14:32');
  });

  it('formats the long date with a genitive month and a comma after the weekday', () => {
    expect(formatDateLong(instant)).toBe('Τετάρτη, 7 Οκτωβρίου 2026');
    expect(formatDateLong('2026-11-06')).toBe('Παρασκευή, 6 Νοεμβρίου 2026');
    expect(formatDateLong(instant, 'el-GR', { weekday: false })).toBe('7 Οκτωβρίου 2026');
    expect(formatDateLong(instant, 'en-GB')).toBe('Wednesday, 7 October 2026');
  });

  it('uses the nominative month for headers and genitive in day labels', () => {
    expect(formatMonthYear(instant)).toBe('Οκτώβριος 2026');
    expect(formatMonthYear('2026-05-01')).toBe('Μάιος 2026');
    expect(formatDayMonth('2026-05-01')).toBe('1 Μαΐου');
  });

  it('formats relative times', () => {
    const now = new Date(instant);
    expect(formatRelative('2026-10-07T11:27:00Z', { now })).toBe('πριν από 5 λεπτά');
    expect(formatRelative('2026-10-06T11:32:00Z', { now })).toBe('χθες');
    expect(formatRelative('2026-10-10T11:32:00Z', { now })).toBe('σε 3 ημέρες');
    expect(formatRelative('2026-10-07T11:27:00Z', { now, region: 'en-GB' })).toBe('5 minutes ago');
  });

  it('knows EET and EEST', () => {
    expect(athensZoneAbbreviation(instant)).toBe('EEST');
    expect(athensZoneAbbreviation('2026-01-15T12:00:00Z')).toBe('EET');
  });

  it('rejects invalid dates', () => {
    expect(() => toDate('not a date')).toThrow(RangeError);
  });
});
