import { useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Cmp } from '../../../api/generated/cmp';

/**
 * Fiscal documents (NX-UI-11): cmp.FiscalDocument.listBySource / get and cmp.FiscalReconciliation.breaks.
 * TODO(hardening): resubmit, bulk-resubmit, correct and cancel are served by the Host (BO-CMP-06) but no screen drives
 * them yet (BO-CMP-14); submissions and annual returns are contract-only.
 */
type Schemas = Cmp['schemas'];
const root = '/api/cmp/v1';

export type FiscalDocumentView = Schemas['FiscalDocumentView'];
export type FiscalDocumentIdentifiers = Schemas['FiscalDocumentIdentifiers'];

/** One document as the server returns it: the document plus the identifiers the channel assigned. */
export interface FiscalDocument {
  document: FiscalDocumentView;
  identifiers: FiscalDocumentIdentifiers;
}

export interface Page<T> {
  items: T[];
  nextCursor?: string | null;
}

export interface FiscalFilter {
  sourceType: string;
  sourceId: string;
}

export const sourceTypes = [
  'INVOICE',
  'CREDIT',
  'TRANSACTION',
  'FEE',
  'COMMISSION',
  'CLAIM_PAYMENT',
] as const;

export function useFiscalDocuments(filter: FiscalFilter) {
  return useQuery({
    queryKey: ['cmp', 'fiscal', 'list', filter],
    queryFn: async ({ signal }) => {
      const page = await apiRequest<Page<{ documents?: FiscalDocument[] }>>(
        `${root}/fiscal-documents/list-by-source`,
        {
          query: {
            limit: 100,
            ...(filter.sourceType ? { sourceType: filter.sourceType } : {}),
            ...(filter.sourceId ? { sourceId: filter.sourceId } : {}),
          },
          signal,
        },
      );
      return page.items.flatMap((item) => item.documents ?? []);
    },
  });
}

export function useFiscalBreaks() {
  return useQuery({
    queryKey: ['cmp', 'fiscal', 'breaks'],
    queryFn: async ({ signal }) => {
      const page = await apiRequest<Page<{ breaks?: FiscalDocument }>>(
        `${root}/fiscal-reconciliation/breaks`,
        { query: { limit: 100 }, signal },
      );
      return page.items.flatMap((item) => (item.breaks ? [item.breaks] : []));
    },
  });
}

export function useFiscalDocument(id: string) {
  return useQuery({
    queryKey: ['cmp', 'fiscal', 'get', id],
    queryFn: ({ signal }) =>
      apiRequest<FiscalDocument>(`${root}/fiscal-documents/${encodeURIComponent(id)}`, { signal }),
  });
}

/** «series-number», or a dash while the channel has not numbered the document. */
export const documentNumber = (d: FiscalDocument): string =>
  d.identifiers.number
    ? [d.identifiers.series, d.identifiers.number].filter(Boolean).join('-')
    : '—';
