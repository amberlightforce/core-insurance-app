import { expect, type APIRequestContext } from '@playwright/test';
import { call, eventually, type Json } from '../api.js';

// Development-only endpoints of claims slice 4.

/** Development-only FS clearing notification (D-SL4-18): POST /api/clm/v1/dev/fs-clearing/notifications (the stub's JSON test format). */
export async function postFsNotification(request: APIRequestContext, token: string, payload: Json): Promise<Json> {
  const result = await call(request, token, 'POST', '/api/clm/v1/dev/fs-clearing/notifications', payload);
  expect(result.status, `post FS notification: ${result.text}`).toBeLessThan(300);
  return result.body;
}

/** Registers the BIL clearing payee account of a counterparty insurer (their party ids exist only at run time; api process only). */
export async function registerClearingPayeeAccount(request: APIRequestContext, token: string, counterpartyCode: string, payeeAccountId: string): Promise<void> {
  const result = await call(request, token, 'POST', '/api/clm/v1/dev/fs-clearing/clearing-payee-accounts', { counterpartyCode, payeeAccountId });
  expect(result.status, `register clearing payee account: ${result.text}`).toBeLessThan(300);
}

export type BankAction = 'issue' | 'clear' | 'return' | 'reject';

/** Manual stub bank (D-SL4-13, Billing:StubBank:Mode=Manual): POST /dev/bank/disbursements/{id}/{issue|clear|return|reject}. */
export async function stubBank(request: APIRequestContext, token: string, disbursementId: string, action: BankAction): Promise<Json> {
  const result = await call(request, token, 'POST', `/dev/bank/disbursements/${encodeURIComponent(disbursementId)}/${action}`, {});
  expect(result.status, `bank stub ${action}: ${result.text}`).toBeLessThan(300);
  return result.body;
}

/** Waits until BIL holds the disbursement at the given state (PascalCase in BIL, compared case-insensitively). */
export async function waitForDisbursement(request: APIRequestContext, token: string, disbursementId: string, state: string): Promise<Json> {
  return eventually(`disbursement ${disbursementId} ${state}`, async () => {
    const read = await call(request, token, 'GET', `/api/bil/v1/disbursements/${disbursementId}`);
    expect(read.status, read.text).toBe(200);
    const view = read.body['disbursement'] as Json;
    return String(view['status']).toUpperCase() === state.toUpperCase() ? view : undefined;
  });
}
