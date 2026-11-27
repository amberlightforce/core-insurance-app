import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';

/**
 * Shapes of the DOC read model (DocumentReader). doc.yaml types the list items and the reprint link as untyped
 * (PRD wording), so these are hand-written from DocumentReader and DocumentsController.
 * TODO(hardening): move to the generated types once doc.yaml types the document view.
 */
export interface DeliveryView {
  deliveryId: string;
  channel: string;
  medium: string;
  status: string;
  proofLevel?: string | null;
  notificationDate?: string | null;
  failureReason?: string | null;
  isReprint: boolean;
}

export interface DocumentView {
  documentId: string;
  documentNumber: string;
  documentType: string;
  requestNo: string;
  /** MODULE/Type/id of the object the document is about. */
  subject: string;
  recipientPartyId: string;
  language: string;
  status: string;
  medium: string;
  archiveId: string;
  contentHash: string;
  createdAt: string;
  deliveries: DeliveryView[];
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
