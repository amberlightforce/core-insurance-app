import { expect, type APIRequestContext } from '@playwright/test';
import { call, type Json } from '../api.js';

// Development-only endpoints of claims slice 4.

/** Development-only FS clearing notification (D-SL4-18): POST /api/clm/v1/dev/fs-clearing/notifications. */
export async function postFsNotification(request: APIRequestContext, token: string, payload: Json): Promise<Json> {
  const result = await call(request, token, 'POST', '/api/clm/v1/dev/fs-clearing/notifications', payload);
  expect(result.status, `post FS notification: ${result.text}`).toBeLessThan(300);
  return result.body;
}

export type BankAction = 'issue' | 'clear' | 'return' | 'reject';

/** Bank stub (D-SL4-13): POST /api/clm/v1/dev/bank/disbursements/{id}/{issue|clear|return|reject}. */
export async function stubBank(request: APIRequestContext, token: string, disbursementId: string, action: BankAction): Promise<Json> {
  const result = await call(request, token, 'POST', `/api/clm/v1/dev/bank/disbursements/${encodeURIComponent(disbursementId)}/${action}`, {});
  expect(result.status, `bank stub ${action}: ${result.text}`).toBeLessThan(300);
  return result.body;
}
