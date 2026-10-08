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

/** Polls until the async outbox (the worker) has produced the expected state. */
export async function eventually<T>(what: string, read: () => Promise<T | undefined>): Promise<T> {
  const deadline = Date.now() + WORKER_TIMEOUT_MS;
  for (;;) {
    const value = await read();
    if (value !== undefined) return value;
    if (Date.now() > deadline) throw new Error(`timed out after ${WORKER_TIMEOUT_MS / 1000}s waiting for the worker: ${what}`);
    await new Promise((done) => setTimeout(done, 1000));
  }
}
