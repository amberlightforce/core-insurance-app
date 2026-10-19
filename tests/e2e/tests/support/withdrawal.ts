import { expect, type APIRequestContext } from '@playwright/test';
import { call, signIn, type Json } from './api.js';
import { advanceClock } from './time.js';
import { serverNow, type Issued } from './servicing.js';

// Helpers of the distance withdrawal journey (SL6-E2E-08): Europe/Athens calendar arithmetic on the shiftable dev clock.

const ZONE = 'Europe/Athens';

/** The Athens offset (ms) at an instant. */
const offsetMs = (at: Date): number => {
  const p = new Intl.DateTimeFormat('en-US', { timeZone: ZONE, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }).formatToParts(at);
  const v = (t: string): number => Number(p.find((x) => x.type === t)!.value);
  return Date.UTC(v('year'), v('month') - 1, v('day'), v('hour'), v('minute'), v('second')) - Math.floor(at.getTime() / 1000) * 1000;
};

/** The instant of an Athens wall-clock time. */
export function athens(date: string, time: string): Date {
  const wall = Date.parse(`${date}T${time}Z`);
  let guess = wall - offsetMs(new Date(wall));
  guess = wall - offsetMs(new Date(guess));
  return new Date(guess);
}

/** The Athens calendar date (yyyy-mm-dd) of an instant. */
export const athensDate = (at: Date): string => new Intl.DateTimeFormat('en-CA', { timeZone: ZONE, year: 'numeric', month: '2-digit', day: '2-digit' }).format(at);
export const athensTime = (at: Date): string => new Intl.DateTimeFormat('en-GB', { timeZone: ZONE, hourCycle: 'h23', hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(at);

export const addDays = (date: string, days: number): string => new Date(Date.parse(`${date}T00:00:00Z`) + days * 86_400_000).toISOString().slice(0, 10);

/** The Athens calendar day a policy was concluded on (bound 20 s before its start). */
export const concludedDate = (policy: Issued): string => athensDate(new Date(policy.startAt.getTime() - 20_000));

/** Moves the dev clock forward to an Athens wall-clock time (forward only; lands within about a second). */
export async function advanceToAthens(request: APIRequestContext, date: string, time: string): Promise<Date> {
  const target = athens(date, time);
  const now = await serverNow(request);
  const seconds = Math.ceil((target.getTime() - now.getTime()) / 1000);
  expect(seconds, `the clock only moves forward (now ${now.toISOString()}, target ${target.toISOString()})`).toBeGreaterThan(0);
  await advanceClock(request, { seconds });
  return serverNow(request);
}

/** The first Athens calendar day on or after `from` on which the UTC offset changes (DST), scanning a year ahead. */
export function nextDstChangeDay(from: string): string {
  for (let i = 0; i < 400; i++) {
    const day = addDays(from, i);
    if (offsetMs(new Date(`${day}T12:00:00Z`)) !== offsetMs(new Date(`${addDays(day, 1)}T12:00:00Z`))) return addDays(day, 1);
  }
  throw new Error('no DST change within a year');
}

export const declare = async (request: APIRequestContext, token: string, policyId: string, tag: string) =>
  call(request, token, 'POST', '/api/chn/v1/withdrawals/declare', { name: 'Μαρία Παπαδοπούλου', contractId: policyId, eMail: `${tag}.withdrawal@example.org`, reason: 'Changed my mind' });

export const confirm = async (request: APIRequestContext, token: string, requestNumber: string, confirmToken: string) =>
  call(request, token, 'POST', '/api/chn/v1/withdrawals/confirm', { requestNumber, token: confirmToken });

export const readWithdrawal = async (request: APIRequestContext, token: string, requestNumber: string): Promise<Json> => {
  const read = await call(request, token, 'GET', `/api/chn/v1/withdrawals/${encodeURIComponent(requestNumber)}`);
  expect(read.status, read.text).toBe(200);
  return read.body;
};

export { signIn };
