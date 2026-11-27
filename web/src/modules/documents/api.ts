import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { components } from '../../api/generated/doc';

type Schemas = components['schemas'];

/** Delivery of a document (generated from doc.yaml). */
export type DeliveryView = Schemas['DocumentDeliverySummary'];

/** A document with its deliveries: the `get`, `listForObject` and `search` item shape. */
export type DocumentView = Schemas['DocumentSearchItem'];

/** One page of the library search. */
export type DocumentSearchPage = Schemas['DocumentSearchPage'];

/** Filters of doc.Document.search (all optional). Dates are yyyy-MM-dd. */
export interface DocumentSearchFilters {
  templateKey?: string;
  status?: string;
  medium?: string;
  subjectModule?: string;
  subjectType?: string;
  subjectId?: string;
  recipientPartyId?: string;
  createdFrom?: string;
  createdTo?: string;
  q?: string;
}

export interface EvidenceView {
  evidenceId: string;
  deliveryId: string;
  provider: string;
  providerEventId: string;
  eventType: string;
  proofDate?: string | null;
  proofReference?: string | null;
  outcome: string;
  receivedAt: string;
}

export interface EvidencePack {
  sealedArchive: { document: DocumentView; evidence: EvidenceView[]; final: boolean };
}

const base = '/api/doc/v1';

/** doc.Document.listForObject; `objectRef` is MODULE/Type/id (business ids, not personal data). */
export function useSubjectDocuments(objectRef: string) {
  return useQuery({
    queryKey: ['doc', 'list', objectRef],
    queryFn: ({ signal }) =>
      apiRequest<{ items: DocumentView[] }>(`${base}/documents/list-for-object`, {
        query: { objectRef },
        signal,
      }),
  });
}

/**
 * doc.Document.search: the document library, newest first. `cursor` is the opaque `nextCursor` of the previous page
 * (keyset). BO-DOC-12 builds the filterable library UI on this.
 */
export function useDocumentSearch(filters: DocumentSearchFilters, cursor?: string, limit = 50) {
  return useQuery({
    queryKey: ['doc', 'search', filters, cursor ?? null, limit],
    queryFn: ({ signal }) =>
      apiRequest<DocumentSearchPage>(`${base}/documents/search`, {
        query: { ...filters, cursor, limit },
        signal,
      }),
  });
}

/** doc.Document.get. */
export function useDocument(documentId: string) {
  return useQuery({
    queryKey: ['doc', 'document', documentId],
    queryFn: ({ signal }) =>
      apiRequest<DocumentView>(`${base}/documents/${encodeURIComponent(documentId)}`, { signal }),
  });
}

/** doc.EvidencePack.get: the document, its deliveries and every provider callback. */
export function useEvidencePack(documentId: string) {
  return useQuery({
    queryKey: ['doc', 'evidence', documentId],
    queryFn: ({ signal }) =>
      apiRequest<EvidencePack>(`${base}/evidence-packs/${encodeURIComponent(documentId)}`, {
        signal,
      }),
  });
}

/** doc.Document.reprint. The caller reuses the Idempotency-Key on retry. */
export function useReprint(documentId: string, onDone: () => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ reason, key }: { reason: string; key: string }) =>
      apiRequest<unknown>(`${base}/documents/reprint`, {
        method: 'POST',
        body: { documentId, reason },
        idempotencyKey: key,
      }),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ['doc'] });
      onDone();
    },
  });
}

export const subjectModules = [
  'POL',
  'CLM',
  'BIL',
  'PTY',
  'UW',
  'RI',
  'FIN',
  'CHN',
  'CMP',
] as const;

/** Semantic pill family of a document status (the status is a free code in the read model). */
export function documentSemantic(status: string): 'success' | 'warning' | 'error' | 'info' {
  const s = status.toLowerCase();
  if (['failed', 'bounced', 'withdrawn', 'error'].some((x) => s.includes(x))) return 'error';
  if (['pending', 'draft', 'queued', 'requested'].some((x) => s.includes(x))) return 'warning';
  if (['delivered', 'issued', 'archived', 'sent'].some((x) => s.includes(x))) return 'success';
  return 'info';
}
