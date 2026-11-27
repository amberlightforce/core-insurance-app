import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components } from '../../../api/generated/bil';

const base = '/api/bil/v1';

export type ReceiptView = components['schemas']['ReceiptView'];
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

/** bil suspense workbench: unapplied cash (receipts in SUSPENSE). */
export function useSuspense() {
  return useQuery({
    queryKey: ['bil', 'ops', 'suspense'],
    queryFn: ({ signal }) =>
      apiRequest<OpsPage<ReceiptView>>(`${base}/billing-ops/suspense`, {
        query: { limit: 100 },
        signal,
      }),
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
