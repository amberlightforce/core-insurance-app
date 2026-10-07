import {
  CalendarDate,
  endOfMonth,
  parseDate,
  startOfMonth,
  startOfYear,
  type DateValue,
} from '@internationalized/date';

import { formatDateLong } from '../../../format/dates';
import type { RegionFormat } from '../../../format/numbers';

/** Staff time zone (Part 4 §8.2). */
export const ATHENS_ZONE = 'Europe/Athens';

export interface Holiday {
  /** A calendar date or an ISO date string ("2026-10-28"). */
  date: CalendarDate | string;
  /** «Επέτειος του Όχι»: shown in the cell's label and tooltip. */
  name: string;
}

/** Holidays by ISO date for quick lookup in the calendar grid. */
export function holidayMap(holidays: readonly Holiday[] | undefined): ReadonlyMap<string, string> {
  const map = new Map<string, string>();
  for (const holiday of holidays ?? []) {
    const key =
      typeof holiday.date === 'string'
        ? parseDate(holiday.date).toString()
        : holiday.date.toString();
    const existing = map.get(key);
    map.set(key, existing ? `${existing} · ${holiday.name}` : holiday.name);
  }
  return map;
}

/** The calendar-date part of any date value. */
export function toCalendarDateOnly(value: DateValue): CalendarDate {
  return new CalendarDate(value.calendar, value.era, value.year, value.month, value.day);
}

/** «Πέμπτη, 6 Νοεμβρίου 2026» (genitive month) in the region format. */
export function formatLongDate(value: DateValue, region: RegionFormat): string {
  return formatDateLong({ year: value.year, month: value.month, day: value.day }, region);
}

/** «14:05» (24-hour) for date-time values; empty for plain dates. */
export function formatTimeOfDay(value: DateValue): string {
  if (!('hour' in value)) return '';
  return `${String(value.hour).padStart(2, '0')}:${String(value.minute).padStart(2, '0')}`;
}

export type SingleQuickPick = 'today' | 'tomorrow' | 'firstOfNextMonth';
export type RangeQuickPick = 'last7Days' | 'currentMonth' | 'previousQuarter' | 'yearToDate';

/** Single-date quick picks (Part 2 §4.5 effective date). */
export function singleQuickPickDate(pick: SingleQuickPick, today: CalendarDate): CalendarDate {
  switch (pick) {
    case 'today':
      return today;
    case 'tomorrow':
      return today.add({ days: 1 });
    case 'firstOfNextMonth':
      return startOfMonth(today.add({ months: 1 }));
  }
}

/** Range quick picks (Part 2 §4.5 ranges). Inclusive bounds. */
export function rangeQuickPickDates(
  pick: RangeQuickPick,
  today: CalendarDate,
): { start: CalendarDate; end: CalendarDate } {
  switch (pick) {
    case 'last7Days':
      return { start: today.subtract({ days: 6 }), end: today };
    case 'currentMonth':
      return { start: startOfMonth(today), end: endOfMonth(today) };
    case 'previousQuarter': {
      const quarterStartMonth = Math.floor((today.month - 1) / 3) * 3 + 1;
      const thisQuarter = new CalendarDate(today.year, quarterStartMonth, 1);
      const start = thisQuarter.subtract({ months: 3 });
      return { start, end: endOfMonth(start.add({ months: 2 })) };
    }
    case 'yearToDate':
      return { start: startOfYear(today), end: today };
  }
}

export const SINGLE_QUICK_PICKS: readonly SingleQuickPick[] = [
  'today',
  'tomorrow',
  'firstOfNextMonth',
];
export const RANGE_QUICK_PICKS: readonly RangeQuickPick[] = [
  'last7Days',
  'currentMonth',
  'previousQuarter',
  'yearToDate',
];
