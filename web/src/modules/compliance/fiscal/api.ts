import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Cmp } from '../../../api/generated/cmp';

/**
 * Fiscal documents (NX-UI-11): cmp.FiscalDocument.listBySource / get and cmp.FiscalReconciliation.breaks.
 * BO-CMP-14 adds the BO-CMP-06 commands (resubmit, bulk-resubmit, cancel, correct) and the QR payload read.
 * TODO(hardening): breaks resolve actions (BO-CMP-07) and annual returns (BO-CMP-10) are not built yet.
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

export type FiscalStatus = 'PENDING' | 'REGISTERED' | 'REJECTED' | 'CANCELLED';

export interface ResubmitResult {
  fiscalDocumentId: string;
  status: FiscalStatus;
  rejectionCodes?: string[];
  resubmitted: boolean;
}
export interface CancelResult {
  fiscalDocumentId: string;
  status: FiscalStatus;
  cancellationFiscalDocumentId?: string | null;
}
export interface CorrectResult {
  originalFiscalDocumentId: string;
  creditFiscalDocumentId: string;
  creditStatus: FiscalStatus;
  correctedFiscalDocumentId: string;
  correctedStatus: FiscalStatus;
}
export interface BulkItem {
  fiscalDocumentId: string;
  outcome: 'RESUBMITTED' | 'SKIPPED';
  status?: FiscalStatus;
  errorCode?: string;
}
export interface BulkResult {
  requested: number;
  resubmitted: number;
  skipped: number;
  results: BulkItem[];
}
export interface Correction {
  fiscalCategoryKey: string;
  amount: { amount: string; currency: string };
  chargeId?: string;
}

export const BULK_LIMIT = 50;

export type FiscalCommand =
  | { name: 'resubmit'; body: { fiscalDocumentId: string; reason: string } }
  | { name: 'cancel'; body: { fiscalDocumentId: string; reason: string } }
  | {
      name: 'correct';
      body: { fiscalDocumentId: string; reason: string; corrections: Correction[] };
    }
  | { name: 'bulk-resubmit'; body: { ids: string[]; reason: string } };

/** Any BO-CMP-06 command; one Idempotency-Key per user action. Success refreshes every fiscal read. */
export function useFiscalCommand() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ command, key }: { command: FiscalCommand; key: string }) =>
      apiRequest<ResubmitResult | CancelResult | CorrectResult | BulkResult>(
        `${root}/fiscal-documents/${command.name}`,
        { method: 'POST', body: command.body, idempotencyKey: key },
      ),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['cmp', 'fiscal'] });
    },
  });
}

export interface QrPayload {
  status: string;
  mark?: string | null;
  uid?: string | null;
  series?: string | null;
  number?: string | null;
  qrPayloadRef?: string | null;
  qrPayload?: string | null;
  stub: boolean;
  layoutVerified: boolean;
}

/** cmp.FiscalDocument.qrPayload; read on demand. The layout is not verified against the tax authority's spec. */
export function useQrPayload(id: string, enabled: boolean) {
  return useQuery({
    queryKey: ['cmp', 'fiscal', 'qr', id],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<QrPayload>(`${root}/fiscal-documents/qr-payload`, { query: { id }, signal }),
  });
}
