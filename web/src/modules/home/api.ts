import { useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { DsarCase } from '../compliance/dsar/api';
import type { ClockItem, ComplaintItem } from '../compliance/home/api';
import { readRecent as readMigrationRecent } from '../migration/api';
import type { ContractListItem } from '../reinsurance/api';
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

export { readMigrationRecent };

interface Items<T> {
  items: T[];
}

/** Reinsurance treaties (ri.Contract.list, first page, all statuses). */
export function useRiContracts(enabled: boolean) {
  return useQuery({
    queryKey: ['ri', 'home', 'contracts'],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<Items<ContractListItem>>('/api/ri/v1/contracts', {
        query: { limit: 100 },
        signal,
      }),
  });
}

/** Compliance: clocks, complaints and DSAR cases; same keys as the compliance screens share their caches. */
export function useComplianceHome(enabled: boolean) {
  const clocks = useQuery({
    queryKey: ['cmp', 'clock', 'query'],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<Items<ClockItem>>('/api/cmp/v1/clocks/query', { query: { limit: 200 }, signal }),
  });
  const complaints = useQuery({
    queryKey: ['cmp', 'complaint', 'query'],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<Items<ComplaintItem>>('/api/cmp/v1/complaints/query', {
        query: { limit: 200 },
        signal,
      }),
  });
  const dsars = useQuery({
    queryKey: ['cmp', 'dsar', 'list'],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<Items<DsarCase>>('/api/cmp/v1/dsar/query', { query: { limit: 200 }, signal }),
  });
  return { clocks, complaints, dsars };
}
