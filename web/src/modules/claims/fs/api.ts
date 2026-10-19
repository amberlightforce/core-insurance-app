import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest, isApiError } from '../../../api/client';
import type { components as Clm } from '../../../api/generated/clm';

/**
 * Friendly Settlement case of the claim file (SL4-UI-CLM-FS, REQ-CLM-155..159). The browser sends ids and the
 * record version only; the clearing value, eligibility and clearing reference always come from the server and the
 * clearing adapter, never from the client (PITFALLS 7).
 */
type S = Clm['schemas'];
export type FsCaseView = S['FsCaseView'];
export type FsEligibility = S['FsEligibility'];
export type FsEvaluateResponse = S['FsEvaluateResponse'];
export type FsOpenOwnSettlementResponse = S['FsOpenOwnSettlementResponse'];
export type FsSubmitResponse = S['FsSubmitResponse'];

const base = '/api/clm/v1/friendly-settlements';

/** The server answers CLM-ERR-FS-DISABLED when the capability is off (Cyprus, Production without a binding). */
export function isFsDisabled(error: unknown): boolean {
  return isApiError(error) && (error.code ?? '').includes('FS-DISABLED');
}

export function useFsCases(claimId: string) {
  return useQuery({
    queryKey: ['clm', 'fs', claimId],
    retry: false,
    queryFn: ({ signal }) =>
      apiRequest<{ items: FsCaseView[] }>(base, { query: { claimId, limit: 20 }, signal }),
  });
}

function useRefresh(claimId: string) {
  const client = useQueryClient();
  return async () => {
    await client.invalidateQueries({ queryKey: ['clm', 'fs', claimId] });
    await client.invalidateQueries({ queryKey: ['clm', 'claim', claimId] });
    await client.invalidateQueries({ queryKey: ['clm', 'recoveries', claimId] });
  };
}

export function useEvaluateFs(claimId: string) {
  const refresh = useRefresh(claimId);
  return useMutation({
    mutationFn: ({ key }: { key: string }) =>
      apiRequest<FsEvaluateResponse>(`${base}/evaluate`, {
        method: 'POST',
        body: { claimId },
        idempotencyKey: key,
      }),
    onSuccess: () => refresh(),
  });
}

export function useOpenOwnSettlement(claimId: string) {
  const refresh = useRefresh(claimId);
  return useMutation({
    mutationFn: ({ fsCaseId, version, key }: { fsCaseId: string; version: number; key: string }) =>
      apiRequest<FsOpenOwnSettlementResponse>(`${base}/open-own-settlement`, {
        method: 'POST',
        body: { fsCaseId, expectedRecordVersion: version },
        idempotencyKey: key,
      }),
    onSuccess: () => refresh(),
  });
}

export function useSubmitFs(claimId: string) {
  const refresh = useRefresh(claimId);
  return useMutation({
    mutationFn: ({ fsCaseId, version, key }: { fsCaseId: string; version: number; key: string }) =>
      apiRequest<FsSubmitResponse>(`${base}/submit`, {
        method: 'POST',
        body: { fsCaseId, expectedRecordVersion: version },
        idempotencyKey: key,
      }),
    onSuccess: () => refresh(),
  });
}
