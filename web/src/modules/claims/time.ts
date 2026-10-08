import { athensOffsetMinutes } from '../quote/time';

/** The UTC instant (`...Z`) of a wall-clock date (`yyyy-mm-dd`) and time (`HH:mm`) in Europe/Athens. */
export function athensInstant(date: string, time: string): string {
  const [y = 0, m = 1, d = 1] = date.split('-').map(Number);
  const [h = 0, mi = 0] = time.split(':').map(Number);
  const wall = Date.UTC(y, m - 1, d, h, mi);
  // The offset at the first estimate decides across a DST change; evaluate once more at the corrected instant.
  const first = wall - athensOffsetMinutes(new Date(wall)) * 60000;
  const instant = new Date(wall - athensOffsetMinutes(new Date(first)) * 60000);
  return instant.toISOString().replace(/\.\d{3}Z$/, 'Z');
}

export const timePattern = /^([01]\d|2[0-3]):[0-5]\d$/;
