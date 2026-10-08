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
  ClaimPaymentView,
  ClaimSearchCriteria,
  ClaimSearchPage,
  ExposureCreateRequest,
  ExposureCreateResponse,
  FnolSubmitRequest,
  FnolSubmitResponse,
  FnolValidateResponse,
  FinancialsGetResponse,
  PayeeAccountCaptureRequest,
  PayeeAccountCaptureResponse,
  PayeeAccountListPage,
  PaymentListPage,
  PolicySearchPage,
  TransactionSetBuildRequest,
  TransactionSetBuildResponse,
  TransactionSetGetResponse,
  TransactionSetSubmitResponse,
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

/** clm.PayeeAccount.capture: registers the account with BIL in the claim's context. The IBAN is in the body only. */
export function capturePayeeAccount(request: PayeeAccountCaptureRequest, idempotencyKey: string) {
  return apiRequest<PayeeAccountCaptureResponse>(`${clm}/payee-accounts/capture`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** clm.TransactionSet.build. A dry run stores nothing (preview only). */
export function buildSet(
  request: TransactionSetBuildRequest,
  idempotencyKey: string,
  dryRun: boolean,
) {
  return apiRequest<TransactionSetBuildResponse>(`${clm}/transaction-sets/build`, {
    method: 'POST',
    body: request,
    idempotencyKey,
    ...(dryRun ? { query: { dryRun: true } } : {}),
  });
}

export function submitSet(setId: string, idempotencyKey: string) {
  return apiRequest<TransactionSetSubmitResponse>(`${clm}/transaction-sets/submit`, {
    method: 'POST',
    body: { setId },
    idempotencyKey,
  });
}

export function fetchSet(setId: string, signal?: AbortSignal) {
  return apiRequest<TransactionSetGetResponse>(
    `${clm}/transaction-sets/${encodeURIComponent(setId)}`,
    { ...(signal ? { signal } : {}) },
  );
}

/**
 * The transaction set an approval request is about: reserve approvals have the subject CLM/TransactionSet/
 * «setId/authorityType/costType»; payment approvals have the payment as subject and cannot be resolved to a claim.
 */
export function setIdOfSubject(ref: { module: string; type: string; id: string }): string | null {
  return ref.module === 'CLM' && ref.type === 'TransactionSet'
    ? (ref.id.split('/')[0] ?? null)
    : null;
}

/** One transaction set; polled while it waits for approval (CLM applies the decisions from the worker). */
export function useTransactionSet(setId: string | null) {
  return useQuery({
    queryKey: ['clm', 'set', setId],
    enabled: setId !== null,
    queryFn: ({ signal }) =>
      apiRequest<TransactionSetGetResponse>(
        `${clm}/transaction-sets/${encodeURIComponent(setId ?? '')}`,
        { signal },
      ),
    refetchInterval: (query) => {
      const status = query.state.data?.set.status;
      return status === 'PENDING_APPROVAL' || status === 'SUBMITTED' ? 5000 : false;
    },
  });
}

export function useFinancials(claimId: string) {
  return useQuery({
    queryKey: ['clm', 'financials', claimId],
    queryFn: ({ signal }) =>
      apiRequest<FinancialsGetResponse>(`${clm}/financials/get`, {
        query: { claim: claimId },
        signal,
      }),
  });
}

export function usePayments(claimId: string) {
  return useQuery({
    queryKey: ['clm', 'payments', claimId],
    queryFn: ({ signal }) =>
      apiRequest<PaymentListPage>(`${clm}/claims/${encodeURIComponent(claimId)}/payments`, {
        signal,
      }),
    refetchInterval: (query) => {
      const items = (query.state.data?.items ?? []) as ClaimPaymentView[];
      const busy = items.some((p) =>
        ['PENDING', 'APPROVED', 'SUBMITTED', 'ISSUED'].includes(p.status),
      );
      return busy ? 5000 : false;
    },
  });
}

export function usePayeeAccounts(claimId: string) {
  return useQuery({
    queryKey: ['clm', 'payee-accounts', claimId],
    queryFn: ({ signal }) =>
      apiRequest<PayeeAccountListPage>(
        `${clm}/claims/${encodeURIComponent(claimId)}/payee-accounts`,
        { signal },
      ),
  });
}

/** Everything money-related on a claim changed (a set was submitted, an approval decided, an account captured). */
export function useRefreshMoney(claimId: string) {
  const client = useQueryClient();
  return () =>
    Promise.all([
      client.invalidateQueries({ queryKey: ['clm', 'financials', claimId] }),
      client.invalidateQueries({ queryKey: ['clm', 'payments', claimId] }),
      client.invalidateQueries({ queryKey: ['clm', 'set'] }),
      client.invalidateQueries({ queryKey: ['clm', 'claim', claimId] }),
      client.invalidateQueries({ queryKey: ['clm', 'payee-accounts', claimId] }),
    ]);
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
