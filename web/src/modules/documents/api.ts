import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import { newIdempotencyKey } from '../../api/idempotency';
import { apiFetch } from '../../dev-auth/devAuth';
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
  /** Include documents hidden from the default lists (BO-DOC-02). */
  includeHidden?: boolean;
}

/** One catalogue document type (doc.DocumentType.list). */
export type DocumentTypeItem = Schemas['DocumentTypeListItem'];
export type ArchiveVerifyResult = Schemas['ArchiveVerifyResponse'];
export type CompletenessReport = Schemas['ArchiveCompletenessResponse'];
export type EvidenceExportResult = Schemas['EvidencePackExportResponse'];

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

/** doc.DocumentType.list: the types for the library's type filter (read once, kept for a while). */
export function useDocumentTypes() {
  return useQuery({
    queryKey: ['doc', 'types'],
    staleTime: 5 * 60_000,
    queryFn: ({ signal }) =>
      apiRequest<{ items: DocumentTypeItem[] }>(`${base}/document-types`, { signal }),
  });
}

/** doc.Archive.completeness (admin): gaps of the archive per month in the range (dates yyyy-MM-dd). */
export function useArchiveCompleteness(from: string, to: string, enabled: boolean) {
  return useQuery({
    queryKey: ['doc', 'completeness', from, to],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<CompletenessReport>(`${base}/archives/completeness`, {
        query: { from, to },
        signal,
      }),
  });
}

export type LifecycleAction = 'resend' | 'hide' | 'unhide' | 'supersede' | 'withdraw';

export interface LifecycleInput {
  action: LifecycleAction;
  reason: string;
  /** supersede only. */
  replacementDocumentId?: string;
  /** resend only; empty means the document's own medium. */
  channel?: string;
  key: string;
}

/** BO-DOC-02 lifecycle commands on one document; one Idempotency-Key per submission, reused on retry. */
export function useDocumentLifecycle(
  documentId: string,
  onDone: (action: LifecycleAction) => void,
) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ action, reason, replacementDocumentId, channel, key }: LifecycleInput) =>
      apiRequest<unknown>(`${base}/documents/${action}`, {
        method: 'POST',
        body: {
          documentId,
          reason,
          ...(action === 'supersede' ? { replacementDocumentId } : {}),
          ...(action === 'resend' && channel ? { channel } : {}),
        },
        idempotencyKey: key,
      }),
    onSuccess: (_data, input) => {
      void client.invalidateQueries({ queryKey: ['doc'] });
      onDone(input.action);
    },
  });
}

/** doc.Archive.verify: recomputes the hash of the stored bytes (a POST, so it carries a fresh Idempotency-Key). */
export function useArchiveVerify() {
  return useMutation({
    mutationFn: (archiveId: string) =>
      apiRequest<ArchiveVerifyResult>(`${base}/archives/verify`, {
        method: 'POST',
        body: { archiveId },
        idempotencyKey: newIdempotencyKey(),
      }),
  });
}

/** doc.EvidencePack.export for one document: a sealed zip stored as an archive entry. */
export function useEvidenceExport() {
  return useMutation({
    mutationFn: ({ documentId, key }: { documentId: string; key: string }) =>
      apiRequest<EvidenceExportResult>(`${base}/evidence-packs/export`, {
        method: 'POST',
        body: { packId: documentId },
        idempotencyKey: key,
      }),
  });
}

/** doc.Archive.download: fetches the stored bytes (with the bearer token) and saves them as a file. */
export async function downloadArchive(archiveId: string, fileName: string): Promise<void> {
  const response = await apiFetch(`${base}/archives/${encodeURIComponent(archiveId)}/content`, {
    method: 'GET',
  });
  if (!response.ok) throw new Error(`HTTP ${String(response.status)}`);
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  try {
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.append(link);
    link.click();
    link.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}

export const documentStatuses = ['Rendered', 'Superseded'] as const;
export const documentMedia = ['EMAIL', 'REGISTERED_POST'] as const;

/** Display state of a document: withdrawn is a Superseded document with a reason and no replacement. */
export function lifecycleState(d: DocumentView): 'withdrawn' | 'superseded' | 'normal' {
  if (d.supersededByDocumentId) return 'superseded';
  if (d.withdrawnReason) return 'withdrawn';
  return 'normal';
}

/** Recipient is a party id: shown as its first block only (a reference, never a name). */
export function maskId(id: string): string {
  return `${id.slice(0, 8)}-****`;
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
  if (['delivered', 'issued', 'archived', 'sent', 'rendered'].some((x) => s.includes(x)))
    return 'success';
  return 'info';
}
