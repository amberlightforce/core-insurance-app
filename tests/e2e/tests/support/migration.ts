import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, type APIRequestContext } from '@playwright/test';
import { call, type Json } from './api.js';

// Helpers of E2E-05 (migration of an in-force motor policy): fixture GR-1, batch calls and a read-only SQL probe for facts the
// public API does not expose (baseline hash, opening flags, the opening receivable). Synthetic data only.

export const GR1 = JSON.parse(readFileSync(resolve(import.meta.dirname, '../../fixtures/legacy-gr1.json'), 'utf8')) as Json;

const DAY_MS = 86_400_000;
const athensDate = (date: Date): string => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Athens' }).format(date);
/** The Athens calendar date `offset` days from `base`, as yyyy-mm-dd. */
export const dayAt = (base: Date, offset: number): string => athensDate(new Date(base.getTime() + offset * DAY_MS));
/** The instant 12:00 UTC on that day, as an ISO instant without milliseconds (never on a day boundary, so the Athens date is stable). */
export const noonAt = (base: Date, offset: number): string => `${dayAt(base, offset)}T12:00:00Z`;

const PROJECT = process.env['E2E_COMPOSE_PROJECT'] ?? 'coreins-e2e05';
/** Runs a read-only query as the postgres superuser inside the stack's database container; returns the trimmed text. */
export function sql(query: string): string {
  return execFileSync('docker', ['compose', '-p', PROJECT, 'exec', '-T', 'postgres', 'psql', '-U', 'postgres', '-d', 'coreins', '-v', 'ON_ERROR_STOP=1', '-tAq', '-c', query], {
    encoding: 'utf8',
  }).trim();
}

export interface BatchRun { batchId: string; dry: Json; started: Json; approved: Json; outcomes: Json[] }

export async function createBatch(request: APIRequestContext, token: string, name: string, items: Json[]): Promise<string> {
  const created = await call(request, token, 'POST', '/api/mig/v1/batches', { name, items });
  expect(created.status, created.text).toBe(200);
  return created.body['batch']['batchId'] as string;
}

export const outcomeNames = (body: Json): string[] => (body['outcomes'] as Json[]).map((o) => o['outcome'] as string);

/** Dry run by the maker, real run by the maker (PENDING_APPROVAL), approval by a different user. */
export async function runBatch(request: APIRequestContext, maker: string, checker: string, name: string, items: Json[], expected = 'IMPORTED'): Promise<BatchRun> {
  const batchId = await createBatch(request, maker, name, items);
  const dry = await call(request, maker, 'POST', '/api/mig/v1/batches/start', { batchId, dryRun: true });
  expect(dry.status, dry.text).toBe(200);
  expect(outcomeNames(dry.body), `dry run of ${name}: ${dry.text}`).toEqual(items.map(() => expected));
  const started = await call(request, maker, 'POST', '/api/mig/v1/batches/start', { batchId, dryRun: false });
  expect(started.status, started.text).toBe(200);
  expect(started.body['batch']['status']).toBe('PENDING_APPROVAL');
  const own = await call(request, maker, 'POST', '/api/mig/v1/batches/approve', { batchId, decision: 'APPROVE' });
  expect(own.status, 'the maker cannot approve their own batch').toBe(403);
  const approved = await call(request, checker, 'POST', '/api/mig/v1/batches/approve', { batchId, decision: 'APPROVE' });
  expect(approved.status, approved.text).toBe(200);
  expect(approved.body['batch']['status']).toBe('COMPLETED');
  return { batchId, dry: dry.body, started: started.body, approved: approved.body, outcomes: approved.body['outcomes'] as Json[] };
}

export async function reverse(request: APIRequestContext, token: string, batchId: string): Promise<Json> {
  const reversed = await call(request, token, 'POST', '/api/mig/v1/batches/reverse', { batchId, dryRun: false });
  expect(reversed.status, reversed.text).toBe(200);
  return reversed.body;
}
