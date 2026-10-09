import { keepPreviousData, useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../../api/client';
import type { components } from '../../../api/generated/pol';

type Schemas = components['schemas'];

/** Contract types of the policy file (generated from pol.yaml; nothing is declared by hand). */
export type PolicyFileResponse = Schemas['PolicyGetResponse'];
export type TermTimelineResponse = Schemas['TermTimelineResponse'];
export type TimelineTransaction = Schemas['TimelineTransaction'];
export type SnapshotGetResponse = Schemas['SnapshotGetResponse'];
export type TermViewModel = Schemas['TermView'];
export type ChargeLineModel = Schemas['ChargeLine'];

const base = '/api/pol/v1';

/**
 * pol.Policy.get as of a date (D-SLC-13: a date-form `validAt` is close of business that day in Athens).
 * `knownAt` is left to the server, which answers with `effectiveKnownAt` (D-SL3-03). Only opaque ids and a date
 * go into the URL (PITFALLS 18).
 */
export function usePolicyAt(
  policyId: string,
  validAt: string,
  { enabled = true, keepPrevious = false }: { enabled?: boolean; keepPrevious?: boolean } = {},
) {
  return useQuery({
    queryKey: ['pol', 'file', 'policy', policyId, validAt],
    enabled,
    ...(keepPrevious ? { placeholderData: keepPreviousData } : { retry: false }),
    queryFn: ({ signal }) =>
      apiRequest<PolicyFileResponse>(`${base}/policies/${encodeURIComponent(policyId)}`, {
        query: { validAt },
        signal,
      }),
  });
}

/** pol.Term.timeline: one term's transactions in sequence order, as known at `effectiveKnownAt`. */
export function useTermTimeline(policyId: string, termId: string, validAt: string, enabled = true) {
  return useQuery({
    queryKey: ['pol', 'file', 'timeline', policyId, termId, validAt],
    enabled,
    retry: false,
    queryFn: ({ signal }) =>
      apiRequest<TermTimelineResponse>(`${base}/terms/timeline`, {
        query: { policyId, termId, validAt },
        signal,
      }),
  });
}

/** pol.Snapshot.get as of a date: only its `supersession` metadata is used here (REQ-POL-007, D-SL3-03c). */
export function useSnapshotSupersession(policyId: string, validAt: string, enabled = true) {
  return useQuery({
    queryKey: ['pol', 'file', 'snapshot', policyId, validAt],
    enabled,
    retry: false,
    queryFn: ({ signal }) =>
      apiRequest<SnapshotGetResponse>(`${base}/snapshots/get`, {
        query: { policyId, validAt },
        signal,
      }),
  });
}
