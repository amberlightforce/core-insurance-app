import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Clm } from '../../../api/generated/clm';
import type { components as Ri } from '../../../api/generated/ri';

/**
 * Recoveries, liability facts, set list and payment corrections of the claim file (SL4-UI-CLM-REC). Cash is only
 * ever recorded from BIL evidence (D-SL4-06): the browser sends ids, versions, reasons and requested amounts; it
 * never sends a role, an approver or a "received" amount.
 */
type S = Clm['schemas'];
export type RecoveryView = S['RecoveryView'];
export type RecoveryCreateRequest = S['RecoveryCreateRequest'];
export type RecoveryMilestoneRequest = S['RecoveryRecordMilestoneRequest'];
export type RecoveryDemandRequest = S['RecoveryDemandRequest'];
export type RecoveryDemandResponse = S['RecoveryDemandResponse'];
export type RecoveryWriteOffRequest = S['RecoveryWriteOffRequest'];
export type LiabilityFacts = S['LiabilityFacts'];
export type TransactionSetListItem = S['TransactionSetListItem'];
export type AuthorityPreviewItem = S['AuthorityPreviewItem'];
export type RecoveryTrace = Ri['schemas']['RecoveryTraceResponse'];

const clm = '/api/clm/v1';
const ri = '/api/ri/v1';

export const OPEN_STATES = [
  'OPEN',
  'DEMANDED',
  'DISPUTED',
  'AGREED',
  'IN_ARBITRATION',
  'IN_LITIGATION',
];

export function useRecoveries(claimId: string, enabled = true) {
  return useQuery({
    queryKey: ['clm', 'recoveries', claimId],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<{ items: RecoveryView[] }>(`${clm}/recoveries`, {
        query: { claimId, limit: 100 },
        signal,
      }),
  });
}

export function useTransactionSets(claimId: string) {
  return useQuery({
    queryKey: ['clm', 'sets', claimId],
    queryFn: ({ signal }) =>
      apiRequest<{ items: TransactionSetListItem[] }>(`${clm}/transaction-sets`, {
        query: { claimId, limit: 50 },
        signal,
      }),
  });
}

/** ri.Recovery.trace for the batch that calculated a recoverable row. */
export function useRecoveryTrace(batchId: string | null) {
  return useQuery({
    queryKey: ['ri', 'trace', batchId],
    enabled: batchId !== null,
    queryFn: ({ signal }) =>
      apiRequest<RecoveryTrace>(`${ri}/recoveries/trace`, {
        query: { batchId: batchId ?? '' },
        signal,
      }),
  });
}

function useRefreshRecoveries(claimId: string) {
  const client = useQueryClient();
  return () =>
    Promise.all([
      client.invalidateQueries({ queryKey: ['clm', 'recoveries', claimId] }),
      client.invalidateQueries({ queryKey: ['clm', 'financials', claimId] }),
      client.invalidateQueries({ queryKey: ['clm', 'sets', claimId] }),
      client.invalidateQueries({ queryKey: ['clm', 'claim', claimId] }),
    ]);
}

export function useCreateRecovery(claimId: string) {
  const refresh = useRefreshRecoveries(claimId);
  return useMutation({
    mutationFn: ({ request, key }: { request: RecoveryCreateRequest; key: string }) =>
      apiRequest<{ recovery: RecoveryView }>(`${clm}/recoveries`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: () => refresh(),
  });
}

export function useRecordMilestone(claimId: string) {
  const refresh = useRefreshRecoveries(claimId);
  return useMutation({
    mutationFn: ({ request, key }: { request: RecoveryMilestoneRequest; key: string }) =>
      apiRequest<{ recovery: RecoveryView }>(`${clm}/recoveries/record-milestone`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: () => refresh(),
  });
}

export function useDemandRecovery(claimId: string) {
  const refresh = useRefreshRecoveries(claimId);
  return useMutation({
    mutationFn: ({ request, key }: { request: RecoveryDemandRequest; key: string }) =>
      apiRequest<RecoveryDemandResponse>(`${clm}/recoveries/demand`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: () => refresh(),
  });
}

export function useWriteOffRecovery(claimId: string) {
  const refresh = useRefreshRecoveries(claimId);
  return useMutation({
    mutationFn: ({ request, key }: { request: RecoveryWriteOffRequest; key: string }) =>
      apiRequest<{ recovery: RecoveryView }>(`${clm}/recoveries/write-off`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: () => refresh(),
  });
}

/** clm.Claim.update with the liability facts only (D-SL4-17). */
export function useUpdateLiability(claimId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({
      facts,
      expectedRecordVersion,
      key,
    }: {
      facts: LiabilityFacts;
      expectedRecordVersion: number;
      key: string;
    }) =>
      apiRequest<unknown>(`${clm}/claims/${encodeURIComponent(claimId)}`, {
        method: 'PATCH',
        body: {
          ids: [claimId],
          expectedRecordVersion,
          changes: { liabilityFacts: facts },
          reason: 'LIABILITY_FACTS',
        },
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['clm', 'claim', claimId] }),
  });
}

export type PaymentCorrection = 'void' | 'stop' | 'reissue';

export function useCorrectPayment(claimId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({
      action,
      body,
      key,
    }: {
      action: PaymentCorrection;
      body: { paymentId: string; reason: string; payeeAccountId?: string };
      key: string;
    }) =>
      apiRequest<unknown>(`${clm}/payments/${action}`, {
        method: 'POST',
        body,
        idempotencyKey: key,
      }),
    onSuccess: () =>
      Promise.all([
        client.invalidateQueries({ queryKey: ['clm', 'payments', claimId] }),
        client.invalidateQueries({ queryKey: ['clm', 'financials', claimId] }),
        client.invalidateQueries({ queryKey: ['clm', 'sets', claimId] }),
      ]),
  });
}
