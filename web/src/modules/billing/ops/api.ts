import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components } from '../../../api/generated/bil';
import type { BillingAccountGetResponse } from '../../../api/types';

const base = '/api/bil/v1';

export type SuspenseListItem = components['schemas']['SuspenseListItem'];

/** bil.Suspense.list page (the generated allOf envelope leaves `items` untyped, so the item type is stated here). */
export interface SuspensePage {
  items: SuspenseListItem[];
  nextCursor: string | null;
  limit?: number;
}
export type SuspenseState = components['schemas']['SuspenseState'];
export type SuspenseSuggestion = components['schemas']['SuspenseSuggestion'];
export type SuspenseSuggestResponse = components['schemas']['SuspenseSuggestResponse'];
export type SuspenseTransferRequest = components['schemas']['SuspenseTransferRequest'];
export type SuspenseTransferResponse = components['schemas']['SuspenseTransferResponse'];
export type SuspenseRefundToSenderRequest = components['schemas']['SuspenseRefundToSenderRequest'];
export type SuspenseRefundToSenderResponse =
  components['schemas']['SuspenseRefundToSenderResponse'];
export const suspenseStates: SuspenseState[] = ['OPEN', 'REFUND_PENDING', 'ALLOCATED', 'REFUNDED'];
export type AllocationAllocateRequest = components['schemas']['AllocationAllocateRequest'];
export type AllocationAllocateResponse = components['schemas']['AllocationAllocateResponse'];

/** Page envelope of the billing-ops reads (BillingOpsReader). */
export interface OpsPage<T> {
  items: T[];
  nextCursor: string | null;
  limit: number;
}

/**
 * Plan instance of an attached policy term (BillingOpsController `plan-instances`).
 * TODO(hardening): not in bil.yaml yet (the contract only has the product-level eligible-plans list), so hand-written.
 */
export interface PlanInstance {
  planInstanceId: string;
  termId: string;
  billingAccountId: string;
  policyId: string;
  policyNumber: string;
  termNumber: number;
  productCode: string;
  planCode: string;
  billMode: string;
  method: string;
  termFrom: string;
  termTo: string;
  cancelledEffective?: string | null;
}

/** SEPA mandate imported by the migration (`migrated-records/mandates`; masked IBAN only). */
export interface MigratedMandate {
  mandateId: string;
  batchId: string;
  billingAccountId: string;
  mandateReference: string;
  signatureDate: string;
  ibanMasked?: string | null;
  status: string;
  origin: string;
}

/** bil.Suspense.list: the unapplied-cash items of the legal entity in one state (default OPEN, BO-BIL-08). */
export function useSuspense(state: SuspenseState = 'OPEN') {
  return useQuery({
    queryKey: ['bil', 'ops', 'suspense', state],
    queryFn: ({ signal }) =>
      apiRequest<SuspensePage>(`${base}/suspense`, {
        query: { limit: 100, state },
        signal,
      }),
  });
}

/**
 * One suspense item by id. The contract has no get-by-id, so the state lists are read in turn until it is found
 * (OPEN first, where a decision is still needed). TODO(hardening): a get-by-id endpoint.
 */
export function useSuspenseItem(id: string | undefined) {
  return useQuery({
    queryKey: ['bil', 'ops', 'suspense-item', id],
    enabled: !!id,
    queryFn: async ({ signal }) => {
      for (const state of suspenseStates) {
        const page = await apiRequest<SuspensePage>(`${base}/suspense`, {
          query: { limit: 200, state },
          signal,
        });
        const found = page.items.find((i) => i.suspenseItemId === id);
        if (found) return found;
      }
      return null;
    },
  });
}

/** The billing account behind an account id (transfer target check, payer of the receipt); enabled for an id only. */
export function useAccountLookup(accountId: string | null) {
  return useQuery({
    queryKey: ['bil', 'account', accountId],
    enabled: !!accountId,
    retry: false,
    queryFn: ({ signal }) =>
      apiRequest<BillingAccountGetResponse>(
        `${base}/billing-accounts/${encodeURIComponent(accountId ?? '')}`,
        { signal },
      ),
    select: (response) => response.account,
  });
}

/** bil.Suspense.suggest: ranked invoice candidates (suggestion only). */
export function useSuggestSuspense() {
  return useMutation({
    mutationFn: ({ suspenseItemId, key }: { suspenseItemId: string; key: string }) =>
      apiRequest<SuspenseSuggestResponse>(`${base}/suspense/suggest`, {
        method: 'POST',
        body: { suspenseItemId },
        idempotencyKey: key,
      }),
  });
}

/** bil.Suspense.transfer: move the cash of an OPEN item to another account of the legal entity. */
export function useTransferSuspense(onDone: (response: SuspenseTransferResponse) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: SuspenseTransferRequest; key: string }) =>
      apiRequest<SuspenseTransferResponse>(`${base}/suspense/transfer`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['bil'] });
      onDone(response);
    },
  });
}

/** bil.Suspense.refundToSender: creates the refund (a second approver decides it on the refunds screen). */
export function useRefundSuspense(onDone: (response: SuspenseRefundToSenderResponse) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: SuspenseRefundToSenderRequest; key: string }) =>
      apiRequest<SuspenseRefundToSenderResponse>(`${base}/suspense/refund-to-sender`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['bil'] });
      onDone(response);
    },
  });
}

export function usePlanInstances(search: string) {
  return useQuery({
    queryKey: ['bil', 'ops', 'plans', search],
    queryFn: ({ signal }) =>
      apiRequest<OpsPage<PlanInstance>>(`${base}/billing-ops/plan-instances`, {
        query: { limit: 100, ...(search ? { search } : {}) },
        signal,
      }),
  });
}

export function useMandates(search: string) {
  return useQuery({
    queryKey: ['bil', 'ops', 'mandates', search],
    queryFn: ({ signal }) =>
      apiRequest<OpsPage<MigratedMandate>>(`${base}/migrated-records/mandates`, {
        query: { limit: 100, ...(search ? { search } : {}) },
        signal,
      }),
  });
}

/** bil.Allocation.allocate: whole open invoices only (each line equals the invoice's open amount). */
export function useAllocateSuspense(onDone: (response: AllocationAllocateResponse) => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ request, key }: { request: AllocationAllocateRequest; key: string }) =>
      apiRequest<AllocationAllocateResponse>(`${base}/allocations/allocate`, {
        method: 'POST',
        body: request,
        idempotencyKey: key,
      }),
    onSuccess: (response) => {
      void client.invalidateQueries({ queryKey: ['bil'] });
      onDone(response);
    },
  });
}

/** Whole cents of a decimal money string (amounts are two-decimal EUR strings; no float sums on display values). */
export const toCents = (amount: string): number => Math.round(Number(amount) * 100);
export const fromCents = (cents: number): string => (cents / 100).toFixed(2);
