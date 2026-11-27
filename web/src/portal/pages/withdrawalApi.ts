import { apiRequest } from '../../api/client';
import type { components } from '../../api/generated/chn';

type S = components['schemas'];
export type WithdrawalDeclareRequest = S['WithdrawalDeclareRequest'];
export type WithdrawalDeclareResponse = S['WithdrawalDeclareResponse'];
export type WithdrawalConfirmResponse = S['WithdrawalConfirmResponse'];

const chn = '/api/chn/v1/withdrawals';

/** Public endpoints (chn.Withdrawal.declare / .confirm): no sign-in; the API rate-limits and the token is one-time. */
export function declareWithdrawal(body: WithdrawalDeclareRequest, idempotencyKey: string) {
  return apiRequest<WithdrawalDeclareResponse>(`${chn}/declare`, {
    method: 'POST',
    body,
    idempotencyKey,
  });
}

export function confirmWithdrawal(requestNumber: string, token: string, idempotencyKey: string) {
  return apiRequest<WithdrawalConfirmResponse>(`${chn}/confirm`, {
    method: 'POST',
    body: { requestNumber, token },
    idempotencyKey,
  });
}
