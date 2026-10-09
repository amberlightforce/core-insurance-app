import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, type APIRequestContext } from '@playwright/test';
import { call, cents, eventually, signIn, type Json } from './api.js';
import { readClock } from './time.js';

// Setup shared by the slice-3 servicing journeys (SL3-E2E): E2E-03 cancellation, E2E-04 renewal and the mid-term change. The
// journeys all start from the E2E-01 policy: a MOTOR-GR private car with MTPL and own damage, ANNUAL plan, invoiced and paid in full.

export const PRODUCT = 'MOTOR-GR';
/** The illustrative tariff prices this car at MTPL 121.50 + own damage 308.50 = 430.00 a year (SLICE-PLAN-3 §3.1: 430.00 x 245/365 = 288.63). */
export const VEHICLE_VALUE = '16323.00';
export const ANNUAL_PREMIUM = 43_000n;
const SEED_DIR = resolve(import.meta.dirname, '../../../../src/CoreIns.Modules.Product/Seed');
export const SEED_10 = process.env['E2E_PRODUCT_SEED'] ?? resolve(SEED_DIR, 'motor-gr.product.json');
export const SEED_11 = process.env['E2E_PRODUCT_SEED_11'] ?? resolve(SEED_DIR, 'motor-gr-1.1.product.json');

/** Imports and locks MOTOR-GR 1.0 and 1.1 (D-SL3-15: terms starting on or after 2026-10-01 resolve to 1.1). Safe to call again. */
export async function importProducts(request: APIRequestContext): Promise<void> {
  const admin = await signIn(request, 'admin');
  for (const path of [SEED_10, SEED_11]) {
    const seed = JSON.parse(readFileSync(path, 'utf8')) as Json;
    const imported = await call(request, admin, 'POST', '/api/pfc/v1/product-versions/import', { definition: seed, lock: true });
    // A second run on the same stack answers a conflict for a version that exists already.
    expect([200, 201, 409], `import ${path}: ${imported.text}`).toContain(imported.status);
  }
}

/** The dev clock's current instant, as the server sees it. */
export async function serverNow(request: APIRequestContext): Promise<Date> {
  return new Date(String((await readClock(request))['now']));
}

export const iso = (date: Date): string => date.toISOString().replace(/\.\d+Z$/, 'Z');

export interface Issued {
  partyId: string;
  policyId: string;
  policyNumber: string;
  termId: string;
  vehicleLocator: string;
  jobId: string;
  invoiceId: string;
  accountId: string;
  premium: bigint;
  tax: bigint;
  total: bigint;
  startAt: Date;
  productVersion: string | undefined;
  quote: Json;
}

export async function createParty(request: APIRequestContext, underwriter: string, tag: string): Promise<string> {
  const created = await call(request, underwriter, 'POST', '/api/pty/v1/parties', {
    partyType: 'PERSON',
    person: { givenNames: 'Μαρία', familyName: 'Παπαδοπούλου', fatherName: 'Γεώργιος', birthDate: '1980-05-17' },
    identifiers: [],
    addresses: [{ types: ['LEGAL', 'MAILING'], country: 'GR', street: 'Λεωφ. Κηφισίας', number: '124', postcode: '11526', locality: 'Αθήνα' }],
    contactPoints: [{ type: 'EMAIL', value: `${tag}.person@example.org`, primary: true }],
    reason: 'NEW_CUSTOMER',
  });
  expect(created.status, created.text).toBe(201);
  return created.body['party']['partyId'] as string;
}

/** Quotes and binds the E2E-01 policy, starting 20 s from the server's now; optionally waits for the invoice and pays it. */
export async function issuePolicy(request: APIRequestContext, tag: string, options: { pay: boolean; plate?: string } = { pay: true }): Promise<Issued> {
  const underwriter = await signIn(request, 'underwriter');
  const billing = await signIn(request, 'billing');
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
      { op: 'SET_VEHICLE', vehicle: { plate: options.plate ?? 'ikx-1234', make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2021, engineCapacityCc: 1400, use: 'PRIVATE', value: { amount: VEHICLE_VALUE, currency: 'EUR' } } },
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
  const bind = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/bind', { jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
  expect(bind.status, bind.text).toBe(200);
  const policyId = bind.body['policyId'] as string;

  const invoiceId = await eventually('the invoice of the bound policy', async () => {
    const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policyId}`);
    expect(list.status, list.text).toBe(200);
    const items = list.body['items'] as Json[];
    return items.length === 1 ? (items[0]!['invoice']['invoiceId'] as string) : undefined;
  });
  const invoice = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${invoiceId}`)).body['invoice'] as Json;
  const accountId = invoice['billingAccountId'] as string;
  if (options.pay) {
    const paid = await call(request, billing, 'POST', '/api/bil/v1/payments/take', { billingAccountId: accountId, amount: invoice['total'], method: 'BANK_TRANSFER', invoiceId });
    expect(paid.status, paid.text).toBe(201);
    await eventually('the invoice to be PAID', async () => {
      const read = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${invoiceId}`)).body;
      return read['invoice']['state'] === 'PAID' ? read : undefined;
    });
  }
  const charges = quote.body['charges'] as Json[];
  const sumOf = (category: string): bigint => charges.filter((c) => c['chargeCategory'] === category).reduce((a, c) => a + cents(c['amount']), 0n);
  return {
    partyId, policyId, policyNumber: bind.body['policyNumber'] as string, termId: bind.body['termId'] as string, vehicleLocator, jobId, invoiceId, accountId,
    premium: sumOf('PREMIUM'), tax: sumOf('TAX'), total: cents(quote.body['total']), startAt: start,
    productVersion: bind.body['productVersion'] as string | undefined, quote: quote.body,
  };
}

/**
 * Moves the dev clock forward by whole days so that the server's now is `daysAfterStart` days (plus the seconds the test has
 * already spent) after the policy's start. Whole days only: the time of day of the start stays, so the Athens calendar day of
 * "now" is exactly start day + daysAfterStart (day counts, e.g. 245/365 on day 120, do not depend on the hour).
 */
export async function advanceTo(request: APIRequestContext, policy: Issued, daysAfterStart: number): Promise<Date> {
  const { advanceClock } = await import('./time.js');
  const now = await serverNow(request);
  const elapsedDays = Math.floor((now.getTime() - policy.startAt.getTime()) / 86_400_000);
  const days = daysAfterStart - elapsedDays;
  expect(days, 'the clock only moves forward').toBeGreaterThan(0);
  await advanceClock(request, { days });
  return serverNow(request);
}

/** True when the route exists (the API answers with anything but the empty 404 of an unmapped route). */
export async function routeExists(request: APIRequestContext, token: string, method: 'GET' | 'POST', path: string): Promise<boolean> {
  const probe = await call(request, token, method, path, method === 'POST' ? {} : undefined);
  return !(probe.status === 404 && probe.text === '');
}

/** Registers and verifies the refund payee account of the policyholder (the IBAN travels in the request body only). */
export async function registerRefundAccount(request: APIRequestContext, partyId: string, iban: string, holderName: string): Promise<string> {
  const billing = await signIn(request, 'billing');
  const created = await call(request, billing, 'POST', '/api/bil/v1/payee-accounts', {
    partyId, purpose: 'REFUND', iban, holderName, source: 'STAFF', evidenceRef: 'e2e-iban-letter',
  });
  expect(created.status, created.text).toBe(201);
  const id = (created.body['payeeAccount']?.['payeeAccountId'] ?? created.body['payeeAccountId']) as string;
  expect(id, created.text).toBeTruthy();
  expect(created.text, 'the IBAN is never returned').not.toContain(iban);
  const checked = await call(request, billing, 'POST', '/api/bil/v1/payee-accounts/verify', { payeeAccountId: id });
  expect(checked.status, checked.text).toBe(200);
  return id;
}
