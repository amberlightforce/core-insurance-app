import { useQueries } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type {
  BillingAccountGetResponse,
  ClaimGetResponse,
  InvoiceGetResponse,
  PolicyGetResponse,
} from '../../api/types';

export type RecentKind = 'policy' | 'claim' | 'invoice' | 'account';

const enc = encodeURIComponent;

/** Resolves the business number of one remembered record from the API (nothing but the id is stored locally). */
async function numberOf(kind: RecentKind, id: string, signal: AbortSignal): Promise<string> {
  switch (kind) {
    case 'policy': {
      const r = await apiRequest<PolicyGetResponse>(`/api/pol/v1/policies/${enc(id)}`, {
        query: { validAt: new Date().toISOString().slice(0, 10) },
        signal,
      });
      return r.policy.policyNumber;
    }
    case 'claim': {
      const r = await apiRequest<ClaimGetResponse>(`/api/clm/v1/claims/${enc(id)}`, { signal });
      return r.claim.summary.claimNumber;
    }
    case 'invoice': {
      const r = await apiRequest<InvoiceGetResponse>(`/api/bil/v1/invoices/${enc(id)}`, {
        signal,
      });
      return r.invoice.invoiceNumber;
    }
    case 'account': {
      const r = await apiRequest<BillingAccountGetResponse>(
        `/api/bil/v1/billing-accounts/${enc(id)}`,
        { signal },
      );
      return r.account.accountNumber;
    }
  }
}

export interface RecentLabel {
  id: string;
  /** The business number once loaded; until then (or if it cannot be loaded) a short form of the id. */
  label: string;
}

/** Display numbers for the remembered ids of one kind, read from the API (cached, so no extra cost on detail pages). */
export function useRecentLabels(kind: RecentKind, ids: string[]): RecentLabel[] {
  const results = useQueries({
    queries: ids.map((id) => ({
      queryKey: ['recent-label', kind, id],
      staleTime: 5 * 60_000,
      retry: false,
      queryFn: ({ signal }: { signal: AbortSignal }) => numberOf(kind, id, signal),
    })),
  });
  return ids.map((id, i) => ({ id, label: results[i]?.data ?? id.slice(0, 8) }));
}
