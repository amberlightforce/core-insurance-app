import { randomUUID } from 'node:crypto';
import { expect, type APIRequestContext } from '@playwright/test';

// Shared helpers of the API-level E2E journeys (E2E-02a and the setup of its UI run). Money is handled as integer cents parsed
// from the decimal strings of the API, never as floating point.

export type Json = Record<string, any>;

export const WORKER_TIMEOUT_MS = 90_000;

export const cents = (money: { amount: string } | string): bigint => {
  const text = typeof money === 'string' ? money : money.amount;
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(text);
  if (!match) throw new Error(`not a decimal amount: ${text}`);
  const fraction = (match[3] ?? '').padEnd(2, '0');
  if (/[^0]/.test(fraction.slice(2))) throw new Error(`more than 2 significant decimals: ${text}`);
  const value = BigInt(match[2]! + fraction.slice(0, 2));
  return match[1] === '-' ? -value : value;
};
export const sum = (values: bigint[]): bigint => values.reduce((a, b) => a + b, 0n);
export const eur = (value: bigint): string => {
  const magnitude = value < 0n ? -value : value;
  return `${value < 0n ? '-' : ''}${magnitude / 100n}.${(magnitude % 100n).toString().padStart(2, '0')}`;
};
export const money = (value: bigint): { amount: string; currency: 'EUR' } => ({ amount: eur(value), currency: 'EUR' });

export async function signIn(request: APIRequestContext, userId: string): Promise<string> {
  const response = await request.post('/api/plt/v1/dev/sign-in', { data: { userId } });
  expect(response.status(), `dev sign-in as ${userId}`).toBe(200);
  return ((await response.json()) as Json)['accessToken'] as string;
}

export async function call(
  request: APIRequestContext,
  token: string,
  method: 'GET' | 'POST',
  path: string,
  body?: unknown,
): Promise<{ status: number; body: Json; text: string }> {
  const headers: Record<string, string> = { authorization: `Bearer ${token}`, 'accept-language': 'en' };
  if (method === 'POST') headers['idempotency-key'] = randomUUID();
  const response = await request.fetch(path, { method, headers, data: body });
  const text = await response.text();
  return { status: response.status(), body: text ? (JSON.parse(text) as Json) : {}, text };
}

export const pause = (ms: number): Promise<void> => new Promise((done) => setTimeout(done, ms));

/** Polls until the async outbox (the worker) has produced the expected state; `read` returns undefined while it has not. Fails loudly on timeout. */
export async function eventually<T>(
  what: string,
  read: () => Promise<T | undefined>,
  options: { timeoutMs?: number; intervalMs?: number } = {},
): Promise<T> {
  const timeoutMs = options.timeoutMs ?? WORKER_TIMEOUT_MS;
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await read();
    if (value !== undefined) return value;
    if (Date.now() > deadline) throw new Error(`timed out after ${timeoutMs / 1000}s waiting for the worker: ${what}`);
    await pause(options.intervalMs ?? 1000);
  }
}

/** Polls until two consecutive reads are equal (the async work has settled); fails loudly if it never does. */
export async function settled<T>(what: string, read: () => Promise<T>, options: { timeoutMs?: number; intervalMs?: number } = {}): Promise<T> {
  let last = await read();
  return eventually(
    what,
    async () => {
      const now = await read();
      if (now === last) return now;
      last = now;
      return undefined;
    },
    { timeoutMs: options.timeoutMs ?? 120_000, intervalMs: options.intervalMs ?? 1000 },
  );
}

/** A negative check (something must NOT happen): `check` must hold for `rounds` consecutive polls; it fails on the first violation. */
export async function staysTrue(what: string, check: () => Promise<boolean>, rounds = 3, intervalMs = 2000): Promise<void> {
  for (let round = 0; round < rounds; round++) {
    if (!(await check())) throw new Error(`violated while it had to hold: ${what}`);
    await pause(intervalMs);
  }
}
