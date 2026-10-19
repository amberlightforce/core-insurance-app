import { expect, type APIRequestContext } from '@playwright/test';
import { call, signIn, type Json } from './api.js';
import { createParty, iso, PRODUCT, serverNow, VEHICLE_VALUE } from './servicing.js';

// Helpers of the slice-5 pack-rollback journey (E2E-12).

export const ENTITY = 'GR-TEST';

/** Drafts and prices a MOTOR-GR quote starting 20 s from now, without binding it. */
export async function openQuote(request: APIRequestContext, tag: string, plate: string): Promise<{ jobId: string; partyId: string; quote: Json }> {
  const underwriter = await signIn(request, 'underwriter');
  const partyId = await createParty(request, underwriter, tag);
  const start = new Date((await serverNow(request)).getTime() + 20_000);
  start.setUTCMilliseconds(0);
  const created = await call(request, underwriter, 'POST', '/api/pol/v1/submissions', {
    policyholderPartyId: partyId, product: PRODUCT, channel: 'STAFF', effectiveAt: iso(start), quoteType: 'FULL',
  });
  expect(created.status, created.text).toBe(201);
  const jobId = created.body['jobId'] as string;
  const first = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
    jobId, versionNo: 1, expectedDraftVersion: 0,
    instructions: [
      { op: 'SET_VEHICLE', vehicle: { plate, make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2021, engineCapacityCc: 1400, use: 'PRIVATE', value: { amount: VEHICLE_VALUE, currency: 'EUR' } } },
      { op: 'SET_ANSWERS', questionSet: { questionSetCode: 'MOTOR-RISK', questionSetVersion: '1', answers: { 'Q-USAGE': 'PRIVATE', 'Q-HIRE-REWARD': 'NO' } } },
    ],
  });
  expect(first.status, first.text).toBe(200);
  const vehicleLocator = first.body['riskTree']['vehicles'][0]['locator'] as string;
  const second = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
    jobId, versionNo: 1, expectedDraftVersion: 1,
    instructions: [
      { op: 'SET_DRIVER', driver: { partyId, driverType: 'MAIN', yearFirstLicensed: 2010, vehicleLocator, usagePercent: 100, claimsLast5Years: 0 } },
      { op: 'SET_COVERAGES', coverages: [
        { coverageCode: 'MTPL', elementLocator: vehicleLocator, selected: true },
        { coverageCode: 'OWN-DAMAGE', elementLocator: vehicleLocator, selected: true },
      ] },
    ],
  });
  expect(second.status, second.text).toBe(200);
  const quote = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/quote', { jobId, versionNo: 1 });
  expect(quote.status, quote.text).toBe(200);
  expect(quote.body['bindable']).toBe(true);
  return { jobId, partyId, quote: quote.body };
}

/** Maker requests, checker approves: one pack rollback or activation. Returns the decided activation. */
export async function rollOrActivate(request: APIRequestContext, kind: 'rollback' | 'schedule-activation', pack: string, version: string, reason: string): Promise<Json> {
  const maker = await signIn(request, 'releasemgr');
  const checker = await signIn(request, 'designauth');
  const body = kind === 'rollback' ? { pack, legalEntity: ENTITY, toVersion: version, reason } : { pack, legalEntity: ENTITY, version, reason };
  const created = await call(request, maker, 'POST', `/api/mkt/v1/packs/${kind}`, body);
  expect(created.status, created.text).toBe(200);
  expect(created.body['status']).toBe('PENDING_APPROVAL');
  const decided = await call(request, checker, 'POST', '/api/mkt/v1/pack-activations/decide', {
    activationId: created.body['activationId'], decision: 'APPROVE', reason: 'Independent checker approves the pack change',
  });
  expect(decided.status, decided.text).toBe(200);
  expect(decided.body['activation']['status']).toBe('ACTIVE');
  return decided.body['activation'] as Json;
}

export async function currentHash(request: APIRequestContext): Promise<string> {
  const token = await signIn(request, 'admin');
  const read = await call(request, token, 'GET', '/api/mkt/v1/configuration/current-hash');
  expect(read.status, read.text).toBe(200);
  return read.body['hash'] as string;
}

/** Counts every FIN journal (paged), once the async posting has settled (two equal reads 4 s apart). */
export async function settledJournalCount(request: APIRequestContext): Promise<number> {
  let last = await journalCount(request);
  for (let i = 0; i < 30; i++) {
    await new Promise((done) => setTimeout(done, 4000));
    const now = await journalCount(request);
    if (now === last) return now;
    last = now;
  }
  throw new Error('FIN journals never settled');
}

async function journalCount(request: APIRequestContext): Promise<number> {
  const finance = await signIn(request, 'finance');
  let count = 0;
  let cursor: string | undefined;
  for (;;) {
    const page = await call(request, finance, 'GET', `/api/fin/v1/journals/query?limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
    expect(page.status, page.text).toBe(200);
    count += (page.body['items'] as Json[]).length;
    cursor = (page.body['nextCursor'] ?? page.body['next'] ?? undefined) as string | undefined;
    if (!cursor) return count;
  }
}
