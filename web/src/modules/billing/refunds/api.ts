import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Bil } from '../../../api/generated/bil';

const base = '/api/bil/v1';

export type RefundView = Bil['schemas']['RefundView'];
export type RefundState = Bil['schemas']['RefundState'];
export type RefundBreakdownLine = Bil['schemas']['RefundBreakdownLine'];
export type RefundNettingLine = Bil['schemas']['RefundNettingLine'];
export type RefundListPage = Bil['schemas']['RefundListPage'];
export type RefundListItem = Bil['schemas']['RefundListItem'];
export type RefundGetResponse = Bil['schemas']['RefundGetResponse'];
export type RefundProposeRequest = Bil['schemas']['RefundProposeRequest'];
export type RefundProposeResponse = Bil['schemas']['RefundProposeResponse'];
export type RefundDecideRequest = Bil['schemas']['RefundDecideRequest'];
export type RefundDecideResponse = Bil['schemas']['RefundDecideResponse'];
export type RefundResubmitRequest = Bil['schemas']['RefundResubmitRequest'];
export type RefundResubmitResponse = Bil['schemas']['RefundResubmitResponse'];
export type PayeeAccountCreateRequest = Bil['schemas']['PayeeAccountCreateRequest'];
export type PayeeAccountCreateResponse = Bil['schemas']['PayeeAccountCreateResponse'];

export interface RefundFilter {
  state?: RefundState;
  billingAccountId?: string;
}

/** bil.Refund.list (ids and state only in the query, never personal data). */
export function useRefunds(filter: RefundFilter) {
  return useQuery({
    queryKey: ['bil', 'refunds', filter],
    queryFn: ({ signal }) =>
      apiRequest<RefundListPage>(`${base}/refunds`, { query: { ...filter, limit: 50 }, signal }),
  });
}

/** bil.Refund.get. */
export function useRefund(refundId: string) {
  return useQuery({
    queryKey: ['bil', 'refund', refundId],
    queryFn: ({ signal }) =>
      apiRequest<RefundGetResponse>(`${base}/refunds/${encodeURIComponent(refundId)}`, { signal }),
  });
}

/** bil.Refund.propose; `dryRun` returns the refund that would be proposed and stores nothing. */
export function proposeRefund(
  request: RefundProposeRequest,
  idempotencyKey: string,
  dryRun = false,
) {
  return apiRequest<RefundProposeResponse>(`${base}/refunds/propose`, {
    method: 'POST',
    body: request,
    idempotencyKey,
    ...(dryRun ? { query: { dryRun: true } } : {}),
  });
}

/** bil.Refund.decide. The server checks authority on the refund total and separation of duties. */
export function decideRefund(request: RefundDecideRequest, idempotencyKey: string) {
  return apiRequest<RefundDecideResponse>(`${base}/refunds/decide`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** bil.Refund.resubmit (rejected or returned refund back to proposed, optionally with a corrected payee). */
export function resubmitRefund(request: RefundResubmitRequest, idempotencyKey: string) {
  return apiRequest<RefundResubmitResponse>(`${base}/refunds/resubmit`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** bil.PayeeAccount.create with purpose REFUND. The IBAN is in the body only (P2). */
export function createRefundPayeeAccount(
  request: Pick<PayeeAccountCreateRequest, 'partyId' | 'iban' | 'holderName'>,
  idempotencyKey: string,
) {
  const body: PayeeAccountCreateRequest = { ...request, purpose: 'REFUND', source: 'STAFF' };
  return apiRequest<PayeeAccountCreateResponse>(`${base}/payee-accounts`, {
    method: 'POST',
    body,
    idempotencyKey,
  });
}

export function useRefreshRefunds() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['bil'] });
}

/** Decide and resubmit mutations: the mutation cache holds ids and comments only, never an IBAN. */
export function useRefundMutation<TRequest, TResponse>(
  run: (request: TRequest, key: string) => Promise<TResponse>,
  onDone: (response: TResponse) => void,
) {
  const refresh = useRefreshRefunds();
  return useMutation({
    mutationFn: ({ request, key }: { request: TRequest; key: string }) => run(request, key),
    onSuccess: (response) => {
      void refresh();
      onDone(response);
    },
  });
}

/** The refunds of a list page (the envelope types items loosely, so the elements are narrowed here once). */
export function refundsOf(page: RefundListPage | undefined): RefundView[] {
  return (page?.items as RefundListItem[] | undefined)?.map((i) => i.refund) ?? [];
}
