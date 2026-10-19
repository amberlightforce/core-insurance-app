import { expect, type APIRequestContext } from '@playwright/test';
import { call, cents, eventually, sum, type Json } from '../api.js';

// FIN journal reads for claims slice 4 (PITFALLS 29: wait for a COMPLETE set, never for the first match).

/** Journal sources as "sourceModule:sourceEventType" (the posting rules of gr-test.finance.v4.json). */
export const SL4_ENTRY_TYPES = {
  reserveChanged: 'CLM:ReserveChanged',
  paymentIssued: 'CLM:PaymentIssued',
  recoveryRecorded: 'CLM:RecoveryRecorded',
  billingEntry: 'BIL:BillingEntryPosted',
  riRecoveryCalculated: 'RI:RecoveryCalculated',
} as const;

export interface JournalFilter {
  policyNumber?: string;
  claimId?: string;
  /** Extra raw query string, e.g. "sourceModule=RI". */
  query?: string;
}

export interface Line {
  journal: string;
  source: string;
  account: string;
  side: string;
  amount: bigint;
  dimensions: Json;
}

export async function readJournals(request: APIRequestContext, token: string, filter: JournalFilter): Promise<Json[]> {
  const parts: string[] = ['limit=100'];
  if (filter.policyNumber) parts.push(`policyNumber=${encodeURIComponent(filter.policyNumber)}`);
  if (filter.claimId) parts.push(`claimId=${encodeURIComponent(filter.claimId)}`);
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

export const sourceOf = (j: Json): string => `${j['sourceModule']}:${j['sourceEventType']}`;

export function linesOf(journals: Json[]): Line[] {
  return journals.flatMap((j) =>
    (j['lines'] as Json[]).map((l) => ({
      journal: j['journalNumber'] as string,
      source: sourceOf(j),
      account: l['account'] as string,
      side: l['side'] as string,
      amount: cents(l['amount']),
      dimensions: (l['dimensions'] ?? {}) as Json,
    })),
  );
}

export const signed = (l: Line): bigint => (l.side === 'DEBIT' ? l.amount : -l.amount);

/** Net (debit minus credit) of an account over the lines, optionally restricted. */
export const net = (lines: Line[], account: string, only: (l: Line) => boolean = () => true): bigint =>
  sum(lines.filter((l) => l.account === account && only(l)).map(signed));

/** Every journal balances (debits = credits) and has at least two lines. */
export function expectAllBalanced(journals: Json[]): void {
  const lines = linesOf(journals);
  for (const journal of journals) {
    const own = lines.filter((l) => l.journal === journal['journalNumber']);
    expect(own.length, `${journal['journalNumber']} has at least two lines`).toBeGreaterThanOrEqual(2);
    expect(sum(own.map(signed)), `${journal['journalNumber']} balances`).toBe(0n);
  }
}

/**
 * Waits until the journals matching the filter satisfy `complete` and then stay unchanged for a further read (the worker posts the
 * facts of one claim asynchronously, per aggregate). Returns the journals.
 */
export async function waitForJournals(
  request: APIRequestContext,
  token: string,
  filter: JournalFilter,
  what: string,
  complete: (journals: Json[]) => boolean,
): Promise<Json[]> {
  let last = -1;
  return eventually(`FIN journals ${JSON.stringify(filter)}: ${what}`, async () => {
    const all = await readJournals(request, token, filter);
    const stable = all.length === last;
    last = all.length;
    return complete(all) && stable ? all : undefined;
  });
}

/** Waits for exactly the expected multiset of journal sources (use when the set is fully known). */
export async function waitForJournalSet(request: APIRequestContext, token: string, filter: JournalFilter, expectedTypes: string[]): Promise<Json[]> {
  const expected = [...expectedTypes].sort();
  return waitForJournals(request, token, filter, expected.join(', '), (all) => JSON.stringify(all.map(sourceOf).sort()) === JSON.stringify(expected));
}

export const countOf = (journals: Json[], source: string): number => journals.filter((j) => sourceOf(j) === source).length;
