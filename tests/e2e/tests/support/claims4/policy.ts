import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { randomUUID } from 'node:crypto';
import { expect, type APIRequestContext } from '@playwright/test';
import { advanceServerPast, serverClockNow } from '../time.js';
import { call, signIn, type Json } from '../api.js';

const SEED = process.env['E2E_PRODUCT_SEED'] ?? resolve(import.meta.dirname, '../../../../../src/CoreIns.Modules.Product/Seed/motor-gr.product.json');

export interface BoundPolicy {
  policyId: string;
  policyNumber: string;
  policyholderPartyId: string;
  /** A loss instant inside the term that is already in the past. */
  lossAt: string;
}

/** A party id of a synthetic person (the injured third party, a claimant). */
export async function createPerson(request: APIRequestContext, token: string, givenNames: string, familyName: string): Promise<string> {
  const created = await call(request, token, 'POST', '/api/pty/v1/parties', {
    partyType: 'PERSON',
    person: { givenNames, familyName, fatherName: 'Γεώργιος', birthDate: '1980-05-17' },
    identifiers: [],
    addresses: [{ types: ['LEGAL', 'MAILING'], country: 'GR', street: 'Πατησίων', number: '10', postcode: '10434', locality: 'Αθήνα' }],
    contactPoints: [{ type: 'EMAIL', value: `${randomUUID().slice(0, 8)}@example.org`, primary: true }],
    reason: 'NEW_CUSTOMER',
  });
  expect(created.status, created.text).toBe(201);
  return created.body['party']['partyId'] as string;
}

/** A synthetic organisation party (an insurer, a reinsurer, a clearing office). */
export async function createOrganisation(request: APIRequestContext, token: string, name: string): Promise<string> {
  const created = await call(request, token, 'POST', '/api/pty/v1/parties', { partyType: 'ORGANISATION', organisation: { legalName: `${name} ${randomUUID().slice(0, 6)}` } });
  expect(created.status, created.text).toBe(201);
  return created.body['party']['partyId'] as string;
}

/** Imports the product, creates a policyholder and binds a MOTOR-GR policy (MTPL and own damage) that is in force now. */
export async function bindMotorPolicy(request: APIRequestContext, plate: string): Promise<BoundPolicy> {
  const admin = await signIn(request, 'admin');
  const underwriter = await signIn(request, 'underwriter');
  const seed = JSON.parse(readFileSync(SEED, 'utf8')) as Json;
  const imported = await call(request, admin, 'POST', '/api/pfc/v1/product-versions/import', { definition: seed, lock: true });
  expect([200, 201, 409], imported.text).toContain(imported.status);

  const party = await createPerson(request, underwriter, 'Νίκος', 'Αλεξίου');
  const start = new Date((await serverClockNow(request)).getTime() + 20_000);
  start.setUTCMilliseconds(0);
  const effectiveAt = start.toISOString().replace('.000Z', 'Z');
  const created = await call(request, underwriter, 'POST', '/api/pol/v1/submissions', { policyholderPartyId: party, product: 'MOTOR-GR', channel: 'STAFF', effectiveAt, quoteType: 'FULL' });
  expect(created.status, created.text).toBe(201);
  const jobId = created.body['jobId'] as string;
  const first = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
    jobId, versionNo: 1, expectedDraftVersion: 0,
    instructions: [
      { op: 'SET_VEHICLE', vehicle: { plate, make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2021, engineCapacityCc: 1400, use: 'PRIVATE', value: { amount: '15000.00', currency: 'EUR' } } },
      { op: 'SET_ANSWERS', questionSet: { questionSetCode: 'MOTOR-RISK', questionSetVersion: '1', answers: { 'Q-USAGE': 'PRIVATE', 'Q-HIRE-REWARD': 'NO' } } },
    ],
  });
  expect(first.status, first.text).toBe(200);
  const vehicle = first.body['riskTree']['vehicles'][0]['locator'] as string;
  const second = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
    jobId, versionNo: 1, expectedDraftVersion: 1,
    instructions: [
      { op: 'SET_DRIVER', driver: { partyId: party, driverType: 'MAIN', yearFirstLicensed: 2008, vehicleLocator: vehicle, usagePercent: 100, claimsLast5Years: 0 } },
      { op: 'SET_COVERAGES', coverages: [
        { coverageCode: 'MTPL', elementLocator: vehicle, selected: true },
        { coverageCode: 'OWN-DAMAGE', elementLocator: vehicle, selected: true },
      ] },
    ],
  });
  expect(second.status, second.text).toBe(200);
  const quoted = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/quote', { jobId, versionNo: 1 });
  expect(quoted.status, quoted.text).toBe(200);
  expect(quoted.body['bindable']).toBe(true);
  const bind = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/bind', { jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
  expect(bind.status, bind.text).toBe(200);

  // The loss is dated one second into the term; wait until that instant is in the past on the server too.
  await advanceServerPast(request, new Date(start.getTime() + 4000));
  return {
    policyId: bind.body['policyId'] as string,
    policyNumber: bind.body['policyNumber'] as string,
    policyholderPartyId: party,
    lossAt: new Date(start.getTime() + 1000).toISOString().replace('.000Z', 'Z'),
  };
}
