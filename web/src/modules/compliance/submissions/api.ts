import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Cmp } from '../../../api/generated/cmp';

/**
 * Regulatory submissions workbench (BO-CMP-14 on BO-CMP-09). The server owns the lifecycle (PLANNED to
 * ACKNOWLEDGED), the maker-checker and the due-date rules; the browser sends ids, decisions and text only.
 * TODO(hardening): annual returns (BO-CMP-10) and XBRL render/validate are not built here.
 */
type Schemas = Cmp['schemas'];
const root = '/api/cmp/v1/submissions';

export type SubmissionView = Schemas['SubmissionView'];
export type SubmissionEvidenceRef = Schemas['SubmissionEvidenceRef'];
export type SubmissionHistoryEntry = Schemas['SubmissionHistoryEntry'];
export type ArchiveCompleteness = Schemas['SubmissionArchiveCompletenessResponse'];

export const submissionStatuses = [
  'PLANNED',
  'IN_PREPARATION',
  'READY',
  'SUBMITTED',
  'ACKNOWLEDGED',
  'REJECTED',
  'RESUBMITTED',
] as const;
export const evidenceKinds = [
  'EVIDENCE_PACK',
  'MART_EXPORT',
  'TAX_RETURN',
  'FISCAL_DOCUMENT',
  'OTHER',
] as const;

export interface SubmissionFilter {
  status: string;
  submissionType: string;
  regulator: string;
}

interface Page<T> {
  items: T[];
  nextCursor?: string | null;
}

export function useSubmissions(filter: SubmissionFilter) {
  return useQuery({
    queryKey: ['cmp', 'submissions', 'query', filter],
    queryFn: ({ signal }) =>
      apiRequest<Page<SubmissionView>>(`${root}/query`, {
        query: {
          limit: 100,
          ...(filter.status ? { status: filter.status } : {}),
          ...(filter.submissionType ? { submissionType: filter.submissionType } : {}),
          ...(filter.regulator ? { regulator: filter.regulator } : {}),
        },
        signal,
      }),
  });
}

export function useSubmission(id: string) {
  return useQuery({
    queryKey: ['cmp', 'submissions', 'get', id],
    queryFn: async ({ signal }) =>
      (
        await apiRequest<{ submission: SubmissionView }>(`${root}/${encodeURIComponent(id)}`, {
          signal,
        })
      ).submission,
  });
}

export function useArchiveCompleteness(from: string, to: string, enabled: boolean) {
  return useQuery({
    queryKey: ['cmp', 'submissions', 'archive', from, to],
    enabled,
    queryFn: ({ signal }) =>
      apiRequest<ArchiveCompleteness>(`${root}/archive-completeness`, {
        query: { from, to },
        signal,
      }),
  });
}

export type SubmissionCommandName =
  | 'create'
  | 'attach-content'
  | 'comment'
  | 'approve'
  | 'evidence-pack'
  | 'record-filing'
  | 'record-response';

export interface SubmissionCommand {
  name: SubmissionCommandName;
  body: Record<string, unknown>;
}

export interface CommandResponse {
  submission: SubmissionView;
  archiveId?: string;
  hash?: string;
  documentCount?: number;
  excludedCount?: number;
}

/** Any cmp.Submission command; one Idempotency-Key per user action. Success refreshes every submissions read. */
export function useSubmissionCommand() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ command, key }: { command: SubmissionCommand; key: string }) =>
      apiRequest<CommandResponse>(`${root}/${command.name}`, {
        method: 'POST',
        body: command.body,
        idempotencyKey: key,
      }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['cmp', 'submissions'] });
    },
  });
}

/** The api stores actors as ids ("dev:officer"); the session carries the canonical ActorKey ("USER:dev:officer"). */
export function sameActor(actorKey: string, other: string | null | undefined): boolean {
  if (!actorKey || !other) return false;
  const bare = (value: string): string => value.replace(/^USER:/, '');
  return bare(actorKey) === bare(other);
}
