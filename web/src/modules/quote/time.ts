/** Date helpers for the quote wizard: staff time is Europe/Athens (D-SLC-13 reads dates in that zone). */
const zone = 'Europe/Athens';

const parts = new Intl.DateTimeFormat('en-US', {
  timeZone: zone,
  year: 'numeric',
  month: '2-digit',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
  hourCycle: 'h23',
});

function athensFields(instant: Date): {
  y: number;
  m: number;
  d: number;
  h: number;
  mi: number;
  s: number;
} {
  const map = new Map<string, string>(parts.formatToParts(instant).map((p) => [p.type, p.value]));
  const num = (type: string) => Number(map.get(type));
  return {
    y: num('year'),
    m: num('month'),
    d: num('day'),
    h: num('hour'),
    mi: num('minute'),
    s: num('second'),
  };
}

/** UTC offset of Europe/Athens at an instant, in minutes (+120 winter, +180 summer). */
export function athensOffsetMinutes(instant: Date): number {
  const f = athensFields(instant);
  const asUtc = Date.UTC(f.y, f.m - 1, f.d, f.h, f.mi, f.s);
  return Math.round((asUtc - Math.floor(instant.getTime() / 1000) * 1000) / 60000);
}

/** Today's calendar date in Athens as `yyyy-mm-dd`. */
export function athensToday(now: Date = new Date()): string {
  const f = athensFields(now);
  return `${String(f.y)}-${String(f.m).padStart(2, '0')}-${String(f.d).padStart(2, '0')}`;
}

/** The date after `date` (`yyyy-mm-dd`), calendar arithmetic only. */
export function addDays(date: string, days: number): string {
  const [y = 0, m = 1, d = 1] = date.split('-').map(Number);
  const next = new Date(Date.UTC(y, m - 1, d + days));
  return next.toISOString().slice(0, 10);
}

/** 00:00 Athens time of a calendar date, as a UTC instant. */
export function athensMidnight(date: string): Date {
  const [y = 0, m = 1, d = 1] = date.split('-').map(Number);
  const guess = Date.UTC(y, m - 1, d);
  const first = guess - athensOffsetMinutes(new Date(guess)) * 60000;
  // Re-evaluate once: the offset at the corrected instant decides across a DST change.
  return new Date(guess - athensOffsetMinutes(new Date(first)) * 60000);
}

/**
 * The `effectiveAt` instant for a chosen start date. New business never starts in the past (REQ-POL-137) and the
 * bind gate re-checks it, so a start date of today means one hour from now; any later date starts at 00:00 Athens.
 */
export function effectiveInstant(date: string, now: Date = new Date()): string {
  const instant =
    date <= athensToday(now) ? new Date(now.getTime() + 60 * 60000) : athensMidnight(date);
  return instant.toISOString().replace(/\.\d{3}Z$/, 'Z');
}
