import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Bil } from '../../../api/generated/bil';
import type { PaymentTakeRequest, PaymentTakeResponse } from '../../../api/types';
import { takePayment } from '../api';

const base = '/api/bil/v1';

export type ReceivableView = Bil['schemas']['ReceivableView'];
export type ReceivableListPage = Bil['schemas']['ReceivableListPage'];
export type ReceivableGetResponse = Bil['schemas']['ReceivableGetResponse'];
export type ReceivableStatus = Bil['schemas']['ReceivableStatus'];
export type ReceivableSourceType = Bil['schemas']['ReceivableSourceType'];
export type DisbursementView = Bil['schemas']['DisbursementRequestResponse'];
export type DisbursementGetResponse = Bil['schemas']['DisbursementGetResponse'];
export type DisbursementApproveReleaseRequest = Bil['schemas']['DisbursementApproveReleaseRequest'];
export type DisbursementApproveReleaseResponse =
  Bil['schemas']['DisbursementApproveReleaseResponse'];

export interface ReceivableFilter {
  status?: ReceivableStatus;
  sourceType?: ReceivableSourceType;
}

/** bil.Receivable.list (ids and enums only in the query, never personal data). */
export function useReceivables(filter: ReceivableFilter) {
  return useQuery({
    queryKey: ['bil', 'receivables', filter],
    queryFn: ({ signal }) =>
      apiRequest<ReceivableListPage>(`${base}/receivables`, {
        query: { ...filter, limit: 100 },
        signal,
      }),
  });
}

/** bil.Receivable.get. */
export function useReceivable(receivableId: string) {
  return useQuery({
    queryKey: ['bil', 'receivable', receivableId],
    queryFn: ({ signal }) =>
      apiRequest<ReceivableGetResponse>(`${base}/receivables/${encodeURIComponent(receivableId)}`, {
        signal,
      }),
  });
}

/** bil.Payment.take against a receivable (by its payment reference). The caller reuses the key on retry. */
export function useRecordReceipt(onDone: (response: PaymentTakeResponse) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: PaymentTakeRequest; key: string }) =>
      takePayment(request, key),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['bil'] });
      onDone(response);
    },
  });
}

/** bil.Disbursement.get. */
export function useDisbursement(disbursementId: string) {
  return useQuery({
    queryKey: ['bil', 'disbursement', disbursementId],
    queryFn: ({ signal }) =>
      apiRequest<DisbursementGetResponse>(
        `${base}/disbursements/${encodeURIComponent(disbursementId)}`,
        { signal },
      ),
  });
}

/**
 * bil.Disbursement.list filtered server-side by sourceType=FS_CLEARING (HD-08c); the client check below only guards
 * against a server that ignores the filter.
 */
export function useReleaseCandidates() {
  return useQuery({
    queryKey: ['bil', 'disbursements', 'releases'],
    queryFn: async ({ signal }) => {
      const page = await apiRequest<{ items?: unknown[] }>(`${base}/disbursements`, {
        query: { sourceType: 'FS_CLEARING', limit: 200 },
        signal,
      });
      return (page.items ?? [])
        .map((i) => (i as { disbursement?: DisbursementView }).disbursement)
        .filter((d): d is DisbursementView => d?.sourceType === 'FS_CLEARING');
    },
  });
}

/** bil.Disbursement.approveRelease; the server enforces Staff.BillingManager and separation of duties. */
export function approveRelease(request: DisbursementApproveReleaseRequest, idempotencyKey: string) {
  return apiRequest<DisbursementApproveReleaseResponse>(`${base}/disbursements/approve-release`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

export function useApproveRelease(onDone: (response: DisbursementApproveReleaseResponse) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: DisbursementApproveReleaseRequest; key: string }) =>
      approveRelease(request, key),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['bil'] });
      onDone(response);
    },
  });
}
