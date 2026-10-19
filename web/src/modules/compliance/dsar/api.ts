import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components as Cmp } from '../../../api/generated/cmp';

/**
 * DSAR console (SL6-UI-CMP-DSAR). The server decides everything that matters: the four-eyes rule on erase/restrict
 * (the approver is not the case creator), the legal hold, NOT_BUILT blockers and the deadline. The browser sends
 * case ids and reasons only; it never sends a role or an approver.
 */
type Schemas = Cmp['schemas'];

/** The generated contract types every field optional; the server always sends these (cmp.yaml DsarRequestView…). */
export interface DsarRequestView {
  dsarId: string;
  requestType: RequestType;
  status: string;
  subjectPartyId: string;
  createdBy: string;
  receivedAt: string;
  deadline: string;
  extended: boolean;
  identityVerified: boolean;
  legalHold: boolean;
  approvedBy?: string | null;
  approvalAction?: string | null;
  completedAt?: string | null;
  refusalReason?: string | null;
  blockers?: string[];
}
export interface DsarOutcomeView {
  module: string;
  recordClass: string;
  kind: string;
  recordCount: number;
  legalBasis?: string | null;
  retentionEnd?: string | null;
  exported?: boolean;
  detail?: string | null;
}
export interface DsarTaskView {
  taskId: string;
  module: string;
  status: string;
  phase: string;
  outcomes?: DsarOutcomeView[];
  redactThirdParty?: boolean;
  attempts: number;
  error?: string | null;
}
export type DsarPackageView = Schemas['DsarPackageView'];
export type DsarCreateRequest = Schemas['DsarCreateRequest'];
export type RequestType = DsarCreateRequest['requestType'];

/** A case as every read/command returns it. `tasks` is absent on export/restrict/erase responses. */
export interface DsarCase {
  request: DsarRequestView;
  tasks?: DsarTaskView[];
  package?: DsarPackageView | null;
  perItemOutcome?: DsarOutcomeView[];
}

export const requestTypes = [
  'ACCESS',
  'PORTABILITY',
  'ERASURE',
  'RESTRICTION',
  'OBJECTION',
] as const;
export const identityMethods = ['ID_DOCUMENT', 'IN_PERSON', 'EXISTING_CREDENTIALS'] as const;
export const caseStatuses = [
  'RECEIVED',
  'IDENTITY_VERIFIED',
  'IN_REVIEW',
  'COMPLETED',
  'REFUSED',
] as const;
export const taskStatuses = ['PENDING', 'DONE', 'FAILED', 'NOT_BUILT'] as const;
export const outcomeKinds = [
  'ERASED',
  'ANONYMISED',
  'RESTRICTED',
  'NOT_HELD',
  'NOT_BUILT',
] as const;

export interface Page<T> {
  items: T[];
  nextCursor: string | null;
  limit?: number;
}

/** One disclosure-log entry (the contract types it as Unspecified; this is what the server emits). */
export interface DisclosureEntry {
  entryId: string;
  subjectPartyId?: string | null;
  dsarId?: string | null;
  recipient: string;
  purpose: string;
  legalBasis: string;
  categories: string[];
  recordedBy: string;
  recordedAt: string;
}

const root = '/api/cmp/v1';

/** cmp.Dsar.query (the API pages by cursor; the console loads the first 200 cases). */
export function useDsarList() {
  return useQuery({
    queryKey: ['cmp', 'dsar', 'list'],
    queryFn: ({ signal }) =>
      apiRequest<Page<DsarCase>>(`${root}/dsar/query`, { query: { limit: 200 }, signal }),
  });
}

/** cmp.Dsar.get. */
export function useDsar(id: string) {
  return useQuery({
    queryKey: ['cmp', 'dsar', 'get', id],
    queryFn: ({ signal }) =>
      apiRequest<DsarCase>(`${root}/dsar/${encodeURIComponent(id)}`, { signal }),
  });
}

/** cmp.Dsar.create. */
export function createDsar(body: DsarCreateRequest, key: string) {
  return apiRequest<DsarCase>(`${root}/dsar`, { method: 'POST', body, idempotencyKey: key });
}

export type DsarCommandName =
  | 'verify-identity'
  | 'start-fan-out'
  | 'retry-task'
  | 'decide-item'
  | 'approve'
  | 'extend'
  | 'refuse'
  | 'export'
  | 'restrict'
  | 'erase';

export interface DsarCommand {
  name: DsarCommandName;
  body: Record<string, unknown>;
}

/**
 * Any cmp.Dsar command on a case. One Idempotency-Key per user action. export/restrict/erase do not return the
 * tasks, so every success refetches the case; the response is handed back to the caller (the export package).
 */
export function useDsarCommand() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ command, key }: { command: DsarCommand; key: string }) =>
      apiRequest<DsarCase>(`${root}/dsar/${command.name}`, {
        method: 'POST',
        body: command.body,
        idempotencyKey: key,
      }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: ['cmp', 'dsar'] });
    },
  });
}

/** cmp.DisclosureLog.query (optionally for one data subject). */
export function useDisclosures(partyId: string | null) {
  return useQuery({
    queryKey: ['cmp', 'disclosure', partyId],
    queryFn: ({ signal }) =>
      // TODO(hardening): the API filters by a `partyId` query parameter only (a pseudonymous id, not personal data).
      apiRequest<Page<{ entries: DisclosureEntry[] }>>(`${root}/disclosure-logs/query`, {
        query: { limit: 200, partyId },
        signal,
      }),
  });
}

export interface DisclosureInput {
  recipient: string;
  purpose: string;
  legalBasis: string;
  categories: string[];
  dsarId: string;
  partyId: string;
}

/** cmp.DisclosureLog.record. */
export function useRecordDisclosure() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ input, key }: { input: DisclosureInput; key: string }) =>
      apiRequest<{ entries: DisclosureEntry[] }>(`${root}/disclosure-logs/record`, {
        method: 'POST',
        body: {
          entry: {
            recipient: input.recipient,
            purpose: input.purpose,
            legalBasis: input.legalBasis,
            categories: input.categories,
            dsarId: input.dsarId,
          },
          subject: { partyId: input.partyId },
        },
        idempotencyKey: key,
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: ['cmp', 'disclosure'] }),
  });
}

/** Modules whose DSAR task is NOT_BUILT: they block completion until their handler exists. */
export const notBuiltModules = (request: DsarRequestView): string[] =>
  (request.blockers ?? [])
    .filter((b) => b.startsWith('NOT_BUILT:'))
    .map((b) => b.slice('NOT_BUILT:'.length));
