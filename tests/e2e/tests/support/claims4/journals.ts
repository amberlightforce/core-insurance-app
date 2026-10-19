import { expect, type APIRequestContext } from '@playwright/test';
import { call, eventually, type Json } from '../api.js';

// Complete-set journal wait for claims slice 4 (PITFALLS 29): RI recovery and FS clearing entry types included.

/** Entry types of the slice 4 journals as "sourceModule:sourceEventType". Event names are guesses until the owning WPs merge. */
export const SL4_ENTRY_TYPES = {
  riRecoveryCalculated: 'RI:RecoveryCalculated',
  riRecoveryCollected: 'RI:RecoveryCollected',
  fsClearingNotified: 'CLM:FsClearingNotified',
  fsClearingSettled: 'CLM:FsClearingSettled',
} as const;

export interface JournalFilter {
  policyNumber?: string;
  claimNumber?: string;
  /** Extra raw query string, e.g. "sourceModule=RI". */
  query?: string;
}

async function readAll(request: APIRequestContext, token: string, filter: JournalFilter): Promise<Json[]> {
  const parts: string[] = ['limit=100'];
  if (filter.policyNumber) parts.push(`policyNumber=${encodeURIComponent(filter.policyNumber)}`);
  if (filter.claimNumber) parts.push(`claimNumber=${encodeURIComponent(filter.claimNumber)}`);
  if (filter.query) parts.push(filter.query);
  const all: Json[] = [];
  let cursor: string | undefined;
  do {
    const page = await call(request, token, 'GET', `/api/fin/v1/journals/query?${parts.join('&')}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
    expect(page.status, page.text).toBe(200);
    all.push(...(page.body['items'] as Json[]).map((i) => i['journal'] as Json));
    cursor = page.body['nextCursor'] ?? undefined;
  } while (cursor);
  return all;
}

/** Waits until the journals matching the filter are exactly the expected set (never the first match; PITFALLS 29). */
export async function waitForJournalSet(request: APIRequestContext, token: string, filter: JournalFilter, expectedTypes: string[]): Promise<Json[]> {
  const expected = [...expectedTypes].sort();
  return eventually(`FIN journals ${JSON.stringify(filter)}: ${expected.join(', ')}`, async () => {
    const all = await readAll(request, token, filter);
    const actual = all.map((j) => `${j['sourceModule']}:${j['sourceEventType']}`).sort();
    return JSON.stringify(actual) === JSON.stringify(expected) ? all : undefined;
  });
}
