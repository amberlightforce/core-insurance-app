import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useMemo } from 'react';

import { apiRequest } from '../../api/client';
import type { components as Uw } from '../../api/generated/uw';

/**
 * The referral workbench reads and writes (SL5-UI-UW-WB). The server decides everything that matters: the
 * «can I decide» preview only hides buttons, `uw.Issue.decide` re-checks authority and separation of duties on
 * every call. Nothing here sends a role, an authority or a limit (PITFALLS 1-7); the browser sends job ids only.
 */
type Schemas = Uw['schemas'];

export type ReferralQueue = Schemas['ReferralQueueCode'];
export type ReferralListItem = Schemas['ReferralListItem'];
export type ReferralQueueCounts = Schemas['ReferralQueueCounts'];
export type ReferralReason = Schemas['ReferralReason'];
export type ReferralView = Schemas['ReferralView'];
export type ReferralIssue = Schemas['ReferralIssue'];
export type IssueDecidability = Schemas['IssueDecidability'];
export type DecidabilityReason = Schemas['DecidabilityReason'];
export type AuthorityPreview = Schemas['AuthorityPreview'];
export type IssueDecideRequest = Schemas['IssueDecideRequest'];
export type IssueDecideResponse = Schemas['IssueDecideResponse'];
export type IssueDecision = Schemas['IssueDecisionCode'];

/** uw.Referral.list page (the generated allOf with PageEnvelope types items as unknown[]). */
export interface ReferralListPage {
  items: ReferralListItem[];
  nextCursor: string | null;
  limit?: number;
  counts: ReferralQueueCounts;
}

/** The views of the rail, in order: the mockup's three plus «Απορρίφθηκαν» (D-SL5-03). */
export const referralQueues = [
  'MINE',
  'OPEN',
  'DECIDED_BY_ME_TODAY',
  'REJECTED',
] as const satisfies readonly ReferralQueue[];

export function isReferralQueue(value: string | null): value is (typeof referralQueues)[number] {
  return referralQueues.some((queue) => queue === value);
}

const pageSize = 50;
const uwRoot = '/api/uw/v1';

/** The count a view shows; the MINE count is null when the legal entity has too many open referrals. */
export function countOf(counts: ReferralQueueCounts | undefined, queue: ReferralQueue) {
  if (!counts) return undefined;
  switch (queue) {
    case 'MINE':
      return counts.mine;
    case 'OPEN':
      return counts.open;
    case 'DECIDED_BY_ME_TODAY':
      return counts.decidedByMeToday;
    case 'REJECTED':
      return counts.rejected;
    case 'APPROVED_TODAY':
      return counts.approvedToday;
  }
}

/** uw.Referral.list for one queue, cursor-paged; the counts of the queues come with every page. */
export function useReferralList(queue: ReferralQueue, enabled = true) {
  const query = useInfiniteQuery({
    queryKey: ['uw', 'referrals', 'list', queue],
    enabled,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      apiRequest<ReferralListPage>(`${uwRoot}/referrals`, {
        query: { queue, limit: pageSize, cursor: pageParam },
        signal,
      }),
    getNextPageParam: (last) => last.nextCursor,
  });
  const pages = query.data?.pages;
  // One array per fetched page set: the table never receives a fresh array while nothing changed (PITFALLS 26).
  const items = useMemo<ReferralListItem[]>(
    () => pages?.flatMap((page) => page.items) ?? [],
    [pages],
  );
  const counts = pages?.[0]?.counts;
  return { query, items, counts };
}

/** uw.Referral.get: the referred job with its issues, facts and the caller's decidability. */
export function useReferral(jobRef: string | null) {
  return useQuery({
    queryKey: ['uw', 'referrals', 'get', jobRef],
    enabled: jobRef !== null,
    queryFn: ({ signal }) =>
      apiRequest<{ referral: ReferralView }>(
        `${uwRoot}/referrals/${encodeURIComponent(jobRef ?? '')}`,
        { signal },
      ),
    select: (data) => data.referral,
  });
}

/** uw.Issue.decide (a command: one Idempotency-Key per user action, reused on retry). */
export function decideIssues(request: IssueDecideRequest, idempotencyKey: string) {
  return apiRequest<IssueDecideResponse>(`${uwRoot}/issues/decide`, {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

/** After a decision the queues, their counts and the open referral are read again. */
export function useRefreshReferrals() {
  const client = useQueryClient();
  return useCallback(() => client.invalidateQueries({ queryKey: ['uw'] }), [client]);
}
