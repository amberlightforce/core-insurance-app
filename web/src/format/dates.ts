import type { RegionFormat } from './numbers';

/**
 * Date and time formatting (Part 4 §8.2). Staff time is Europe/Athens, 24-hour, in both languages.
 * - short «07/10/2026», date-time «07/10/2026 14:32», time «14:32»
 * - long «Τετάρτη, 7 Οκτωβρίου 2026» (genitive month), month header «Οκτώβριος 2026» (nominative)
 * - relative «πριν από 5 λεπτά», «χθες» (CLDR wording via Intl.RelativeTimeFormat)
 * The format follows the region format; month and weekday names follow the region's language.
 */
export const ATHENS = 'Europe/Athens';

export type DateInput = Date | string | number | { year: number; month: number; day: number };

/** Accepts a Date, an ISO string, epoch ms, or a calendar date `{year, month, day}` (1-based month). */
export function toDate(value: DateInput): Date {
  if (value instanceof Date) return value;
  if (typeof value === 'number') return new Date(value);
  if (typeof value === 'string') {
    // A bare ISO date is a calendar date: anchor it at noon UTC so every European zone shows the same day.
    if (/^\d{4}-\d{2}-\d{2}$/.test(value)) return new Date(`${value}T12:00:00Z`);
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) throw new RangeError(`Invalid date: ${value}`);
    return date;
  }
  return new Date(Date.UTC(value.year, value.month - 1, value.day, 12));
}

function formatter(region: RegionFormat, options: Intl.DateTimeFormatOptions, timeZone: string) {
  return new Intl.DateTimeFormat(region, { timeZone, ...options });
}

/** «07/10/2026» */
export function formatDate(
  value: DateInput,
  region: RegionFormat = 'el-GR',
  timeZone = ATHENS,
): string {
  return formatter(region, { day: '2-digit', month: '2-digit', year: 'numeric' }, timeZone).format(
    toDate(value),
  );
}

/** «14:32» (24-hour). */
export function formatTime(
  value: DateInput,
  region: RegionFormat = 'el-GR',
  timeZone = ATHENS,
): string {
  return formatter(
    region,
    { hour: '2-digit', minute: '2-digit', hourCycle: 'h23' },
    timeZone,
  ).format(toDate(value));
}

/** «07/10/2026 14:32» (no comma between date and time). */
export function formatDateTime(
  value: DateInput,
  region: RegionFormat = 'el-GR',
  timeZone = ATHENS,
): string {
  return `${formatDate(value, region, timeZone)} ${formatTime(value, region, timeZone)}`;
}

/** «Τετάρτη, 7 Οκτωβρίου 2026» / «Wednesday, 7 October 2026» (genitive month in Greek). */
export function formatDateLong(
  value: DateInput,
  region: RegionFormat = 'el-GR',
  { weekday = true, timeZone = ATHENS }: { weekday?: boolean; timeZone?: string } = {},
): string {
  const date = toDate(value);
  const parts = formatter(
    region,
    { ...(weekday ? { weekday: 'long' } : {}), day: 'numeric', month: 'long', year: 'numeric' },
    timeZone,
  ).formatToParts(date);
  // Greek CLDR omits the comma after the weekday; the guide's long format has it (Part 4 §8.2).
  let seenWeekday = false;
  return parts
    .map((part) => {
      if (part.type === 'weekday') {
        seenWeekday = true;
        return part.value;
      }
      if (seenWeekday && part.type === 'literal') {
        seenWeekday = false;
        return part.value.includes(',') ? part.value : `,${part.value}`;
      }
      return part.value;
    })
    .join('');
}

/** «7 Οκτωβρίου» (genitive, no year): sticky day headers and holiday labels. */
export function formatDayMonth(
  value: DateInput,
  region: RegionFormat = 'el-GR',
  timeZone = ATHENS,
): string {
  return formatter(region, { day: 'numeric', month: 'long' }, timeZone).format(toDate(value));
}

/** «Οκτώβριος 2026» — the standalone (nominative) month for calendar headers. */
export function formatMonthYear(
  value: DateInput,
  region: RegionFormat = 'el-GR',
  timeZone = ATHENS,
): string {
  return formatter(region, { month: 'long', year: 'numeric' }, timeZone).format(toDate(value));
}

const relativeUnits: [Intl.RelativeTimeFormatUnit, number][] = [
  ['year', 365 * 24 * 3600],
  ['month', 30 * 24 * 3600],
  ['week', 7 * 24 * 3600],
  ['day', 24 * 3600],
  ['hour', 3600],
  ['minute', 60],
  ['second', 1],
];

/** «πριν από 5 λεπτά», «σε 3 ημέρες», «χθες» relative to `now`. Use in activity columns only. */
export function formatRelative(
  value: DateInput,
  { now = new Date(), region = 'el-GR' }: { now?: Date; region?: RegionFormat } = {},
): string {
  const seconds = Math.round((toDate(value).getTime() - now.getTime()) / 1000);
  const rtf = new Intl.RelativeTimeFormat(region === 'el-GR' ? 'el' : 'en-GB', { numeric: 'auto' });
  for (const [unit, size] of relativeUnits) {
    if (Math.abs(seconds) >= size || unit === 'second') {
      return rtf.format(Math.round(seconds / size), unit);
    }
  }
  return rtf.format(0, 'second');
}

/** «EET» or «EEST» for an instant in Athens; shown only where cross-zone confusion is possible. */
export function athensZoneAbbreviation(value: DateInput = new Date()): 'EET' | 'EEST' {
  const date = toDate(value);
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: ATHENS,
    timeZoneName: 'longOffset',
  }).formatToParts(date);
  const offset = parts.find((p) => p.type === 'timeZoneName')?.value ?? 'GMT+02:00';
  return offset.includes('+03') ? 'EEST' : 'EET';
}
