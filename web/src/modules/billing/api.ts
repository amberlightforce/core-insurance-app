import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type {
  BillingAccountGetResponse,
  InvoiceGetResponse,
  InvoiceListPage,
  PaymentTakeRequest,
  PaymentTakeResponse,
} from '../../api/types';

const base = '/api/bil/v1';

export function useBillingAccount(accountId: string) {
  return useQuery({
    queryKey: ['bil', 'account', accountId],
    queryFn: ({ signal }) =>
      apiRequest<BillingAccountGetResponse>(
        `${base}/billing-accounts/${encodeURIComponent(accountId)}`,
        { signal },
      ),
  });
}

export function useInvoice(invoiceId: string) {
  return useQuery({
    queryKey: ['bil', 'invoice', invoiceId],
    queryFn: ({ signal }) =>
      apiRequest<InvoiceGetResponse>(`${base}/invoices/${encodeURIComponent(invoiceId)}`, {
        signal,
      }),
  });
}

export interface InvoiceFilter {
  billingAccountId?: string;
  policyId?: string;
}

/** bil.Invoice.list by billing account or policy (ids only, never personal data in the query). */
export function useInvoices(filter: InvoiceFilter, enabled = true) {
  return useQuery({
    queryKey: ['bil', 'invoices', filter],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<InvoiceListPage>(`${base}/invoices`, { query: { ...filter, limit: 50 }, signal }),
  });
}

/** bil.Payment.take (a command: Idempotency-Key per user action, reused on retry). */
export function takePayment(request: PaymentTakeRequest, idempotencyKey: string) {
  return apiRequest<PaymentTakeResponse>(`${base}/payments/take`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

export function useRefreshBilling() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: ['bil'] });
}

export function useTakePayment(onDone: (response: PaymentTakeResponse) => void) {
  const refresh = useRefreshBilling();
  return useMutation({
    mutationFn: ({ request, key }: { request: PaymentTakeRequest; key: string }) =>
      takePayment(request, key),
    onSuccess: (response) => {
      void refresh();
      onDone(response);
    },
  });
}
