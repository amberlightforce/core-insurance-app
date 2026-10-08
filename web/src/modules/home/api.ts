import { useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { ApprovalListPage, InvoiceListPage } from '../../api/types';

/**
 * Home dashboard reads. Only lists that the slice's APIs return without search criteria are used: invoices
 * (`bil.Invoice.list`, no filter) and pending approvals (`plt.Approval.list`). Policy, party and claim searches
 * refuse empty criteria, so those cards use the recently opened records kept in this browser instead.
 * The query keys match the module screens' keys, so the caches are shared.
 */
export function useAllInvoices(enabled: boolean) {
  return useQuery({
    queryKey: ['bil', 'invoices', {}],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<InvoiceListPage>('/api/bil/v1/invoices', { query: { limit: 50 }, signal }),
  });
}

export function usePendingApprovals(enabled: boolean) {
  return useQuery({
    queryKey: ['plt', 'approvals', 'PendingApproval'],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<ApprovalListPage>('/api/plt/v1/approval', {
        query: { status: 'PendingApproval', limit: 50 },
        signal,
      }),
  });
}
