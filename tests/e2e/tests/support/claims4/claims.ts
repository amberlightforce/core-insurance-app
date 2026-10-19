import { expect, type APIRequestContext } from '@playwright/test';
import { call, eventually, money, type Json } from '../api.js';
import type { BoundPolicy } from './policy.js';

// Claim-side helpers shared by the slice 4 journeys. Money is bigint cents (see ../api.ts).

export const IBAN = 'GR1601101250000000012300695';

export interface Fnol {
  claimId: string;
  claimNumber: string;
  claim: Json;
  exposures: Json[];
  /** The stored liability facts (the FNOL answer does not carry them; they come from clm.Claim.get). */
  liabilityFacts: Json;
}

export async function submitFnol(request: APIRequestContext, token: string, policy: BoundPolicy, body: Json): Promise<Fnol> {
  const submitted = await call(request, token, 'POST', '/api/clm/v1/fnol/submit', {
    lineOfBusiness: 'MOTOR',
    policyId: policy.policyId,
    policyNumber: policy.policyNumber,
    lossAt: policy.lossAt,
    lossCause: 'COLLISION',
    lossLocation: 'Λεωφ. Κηφισίας 124, Αθήνα',
    channel: 'STAFF',
    receiptMedium: 'TELEPHONE',
    incidents: [{ incidentType: 'VEHICLE', vehicleRef: 'IKX1234', drivable: true, damageAreas: ['FRONT'] }],
    ...body,
  });
  expect(submitted.status, submitted.text).toBe(200);
  const stored = await call(request, token, 'GET', `/api/clm/v1/claims/${submitted.body['claimId']}`);
  expect(stored.status, stored.text).toBe(200);
  return {
    claimId: submitted.body['claimId'], claimNumber: submitted.body['claimNumber'], claim: submitted.body['claim'], exposures: submitted.body['exposures'],
    liabilityFacts: stored.body['claim']['liabilityFacts'],
  };
}

/** Adds an exposure to an open claim (the current record version is read first). */
export async function addExposure(request: APIRequestContext, token: string, claimId: string, kind: string, coverageCode: string, claimantPartyId?: string): Promise<Json> {
  const current = await call(request, token, 'GET', `/api/clm/v1/claims/${claimId}`);
  expect(current.status, current.text).toBe(200);
  const created = await call(request, token, 'POST', '/api/clm/v1/exposures', {
    claimId, expectedRecordVersion: current.body['claim']['summary']['recordVersion'], kind, coverageCode, claimantPartyId,
  });
  expect(created.status, `add exposure ${kind}: ${created.text}`).toBeLessThan(300);
  return created.body['exposure'] as Json;
}

export async function capturePayee(request: APIRequestContext, token: string, claimId: string, partyId: string, holderName: string): Promise<string> {
  const captured = await call(request, token, 'POST', '/api/clm/v1/payee-accounts/capture', { claimId, partyId, iban: IBAN, holderName });
  expect(captured.status, captured.text).toBe(201);
  return captured.body['payeeAccount']['payeeAccountId'] as string;
}

export const reserveLine = (exposureId: string, amount: bigint, reason = 'INITIAL_ESTIMATE') => ({
  kind: 'RESERVE', exposureId, costType: 'INDEMNITY', costCategory: 'VEHICLE_REPAIR', amount: money(amount), reason,
});

export const recoveryReserveLine = (exposureId: string, recoveryId: string, amount: bigint) => ({
  kind: 'RECOVERY_RESERVE', recoveryId, exposureId, costType: 'INDEMNITY', costCategory: 'VEHICLE_REPAIR', amount: money(amount), reason: 'ASSESSMENT',
});

export const paymentLine = (exposureId: string, amount: bigint, payeePartyId: string, payeeAccountId: string, paymentType = 'FINAL') => ({
  kind: 'PAYMENT', exposureId, costType: 'INDEMNITY', costCategory: 'VEHICLE_REPAIR', amount: money(amount), payeePartyId, payeeAccountId, paymentType,
});

export async function buildSet(request: APIRequestContext, token: string, claimId: string, transactions: unknown[]): Promise<Json> {
  const built = await call(request, token, 'POST', '/api/clm/v1/transaction-sets/build', { claimId, transactions });
  expect(built.status, `build set: ${built.text}`).toBe(200);
  return built.body;
}

export async function getSet(request: APIRequestContext, token: string, setId: string): Promise<Json> {
  const read = await call(request, token, 'GET', `/api/clm/v1/transaction-sets/${setId}`);
  expect(read.status, read.text).toBe(200);
  return read.body['set'] as Json;
}

/** Approves every pending PLT approval of a set as `approver`. */
export async function approvePending(request: APIRequestContext, approver: string, setView: Json): Promise<void> {
  const pending = (setView['approvals'] as Json[]).filter((a) => a['status'] === 'PENDING');
  expect(pending.length, 'the set has approvals to decide').toBeGreaterThan(0);
  for (const approval of pending) {
    const read = await call(request, approver, 'GET', `/api/plt/v1/approval/${approval['approvalRequestId']}`);
    expect(read.status, read.text).toBe(200);
    const detail = (read.body['request'] ?? read.body) as Json;
    const decided = await call(request, approver, 'POST', '/api/plt/v1/approval/decide', { requestId: approval['approvalRequestId'], decision: 'Approve', payloadHash: detail['payloadHash'] });
    expect(decided.status, decided.text).toBe(200);
  }
}

/** Build and submit a set as `maker`; when it is referred, `approver` approves; waits until the set has left PENDING_APPROVAL. */
export async function applySet(request: APIRequestContext, maker: string, approver: string, claimId: string, transactions: unknown[]): Promise<Json> {
  const built = await buildSet(request, maker, claimId, transactions);
  const submitted = await call(request, maker, 'POST', '/api/clm/v1/transaction-sets/submit', { setId: built['setId'] });
  expect(submitted.status, `submit set: ${submitted.text}`).toBe(200);
  if (submitted.body['status'] === 'PENDING_APPROVAL') {
    await approvePending(request, approver, await getSet(request, maker, built['setId']));
  }
  return eventually(`set ${built['setId']} to be APPROVED`, async () => {
    const view = await getSet(request, maker, built['setId']);
    expect(['PENDING_APPROVAL', 'APPROVED', 'POSTED'], `set status ${view['status']}`).toContain(view['status']);
    return view['status'] === 'PENDING_APPROVAL' ? undefined : view;
  });
}

export async function financials(request: APIRequestContext, token: string, claimId: string): Promise<Json> {
  const read = await call(request, token, 'GET', `/api/clm/v1/financials/get?claim=${claimId}`);
  expect(read.status, read.text).toBe(200);
  return read.body;
}

export async function listPayments(request: APIRequestContext, token: string, claimId: string): Promise<Json[]> {
  const list = await call(request, token, 'GET', `/api/clm/v1/claims/${claimId}/payments`);
  expect(list.status, list.text).toBe(200);
  return list.body['items'] as Json[];
}

export async function waitForPayment(request: APIRequestContext, token: string, claimId: string, paymentId: string, status: string): Promise<Json> {
  return eventually(`payment ${paymentId} ${status}`, async () => {
    const payment = (await listPayments(request, token, claimId)).find((p) => p['claimPaymentId'] === paymentId);
    return payment?.['status'] === status ? payment : undefined;
  });
}
