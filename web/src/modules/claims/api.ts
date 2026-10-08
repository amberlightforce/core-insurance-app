import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest, isApiError } from '../../api/client';
import type {
  ApprovalDecideRequest,
  ApprovalDecideResponse,
  ApprovalGetResponse,
  ApprovalListPage,
  ClaimCloseRequest,
  ClaimCloseResponse,
  ClaimGetResponse,
  ClaimSearchCriteria,
  ClaimSearchPage,
  ExposureCreateRequest,
  ExposureCreateResponse,
  FnolSubmitRequest,
  FnolSubmitResponse,
  FnolValidateResponse,
  PayeeAccountCreateRequest,
  PayeeAccountCreateResponse,
  PolicySearchPage,
} from '../../api/types';

const clm = '/api/clm/v1';

/** pol.Policy.searchByCriteria: the policy number travels in the POST body, never in a URL (D-SLC-05). */
export function searchPolicies(policyNumber: string, signal?: AbortSignal) {
  return apiRequest<PolicySearchPage>('/api/pol/v1/policies/search', {
    method: 'POST',
    body: { policyNumber },
    query: { limit: 5 },
    ...(signal ? { signal } : {}),
  });
}

/** clm.Fnol.validate: a read, no Idempotency-Key. */
export function validateFnol(fnol: FnolSubmitRequest) {
  return apiRequest<FnolValidateResponse>(`${clm}/fnol/validate`, {
    method: 'POST',
    body: { fnol },
  });
}

/** clm.Fnol.submit (a command: Idempotency-Key per user action, reused on retry). */
export function submitFnol(fnol: FnolSubmitRequest, idempotencyKey: string) {
  return apiRequest<FnolSubmitResponse>(`${clm}/fnol/submit`, {
    method: 'POST',
    body: fnol,
    idempotencyKey,
  });
}

/** clm.Claim.searchByCriteria: claim and policy numbers travel in the body. */
export function searchClaims(criteria: ClaimSearchCriteria, signal?: AbortSignal) {
  return apiRequest<ClaimSearchPage>(`${clm}/claims/search`, {
    method: 'POST',
    body: criteria,
    query: { limit: 25 },
    ...(signal ? { signal } : {}),
  });
}

export function useClaim(claimId: string) {
  return useQuery({
    queryKey: ['clm', 'claim', claimId],
    queryFn: ({ signal }) =>
      apiRequest<ClaimGetResponse>(`${clm}/claims/${encodeURIComponent(claimId)}`, { signal }),
  });
}

export function useRefreshClaim(claimId: string) {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['clm', 'claim', claimId] });
}

export function createExposure(request: ExposureCreateRequest, idempotencyKey: string) {
  return apiRequest<ExposureCreateResponse>(`${clm}/exposures`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

export function closeClaim(request: ClaimCloseRequest, idempotencyKey: string) {
  return apiRequest<ClaimCloseResponse>(`${clm}/claims/close`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** bil.PayeeAccount.create. The IBAN is in the request body only; it is never part of a URL, key or log. */
export function createPayeeAccount(request: PayeeAccountCreateRequest, idempotencyKey: string) {
  return apiRequest<PayeeAccountCreateResponse>('/api/bil/v1/payee-accounts', {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** plt.Approval.list for one status (the inbox shows PendingApproval). */
export function useApprovals(status: string) {
  return useQuery({
    queryKey: ['plt', 'approvals', status],
    queryFn: ({ signal }) =>
      apiRequest<ApprovalListPage>('/api/plt/v1/approval', {
        query: { status, limit: 50 },
        signal,
      }),
  });
}

export function useApproval(requestId: string) {
  return useQuery({
    queryKey: ['plt', 'approval', requestId],
    queryFn: ({ signal }) =>
      apiRequest<ApprovalGetResponse>(`/api/plt/v1/approval/${encodeURIComponent(requestId)}`, {
        signal,
      }),
  });
}

/** plt.Approval.decide (a command). */
export function decideApproval(request: ApprovalDecideRequest, idempotencyKey: string) {
  return apiRequest<ApprovalDecideResponse>('/api/plt/v1/approval/decide', {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

export function useRefreshApprovals() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['plt'] });
}

/** A write that lost a race (CLM-ERR-STALE) reloads the claim so the next attempt carries the current version. */
function refreshOnStale(error: unknown, refresh: () => unknown) {
  if (isApiError(error) && error.code === 'CLM-ERR-STALE') void refresh();
}

export function useCreateExposure(claimId: string) {
  const refresh = useRefreshClaim(claimId);
  return useMutation({
    mutationFn: ({ request, key }: { request: ExposureCreateRequest; key: string }) =>
      createExposure(request, key),
    onSuccess: () => refresh(),
    onError: (error) => {
      refreshOnStale(error, refresh);
    },
  });
}

export function useCloseClaim(claimId: string) {
  const refresh = useRefreshClaim(claimId);
  return useMutation({
    mutationFn: ({ request, key }: { request: ClaimCloseRequest; key: string }) =>
      closeClaim(request, key),
    onSuccess: () => refresh(),
    onError: (error) => {
      refreshOnStale(error, refresh);
    },
  });
}
