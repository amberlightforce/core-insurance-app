import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback } from 'react';
import { useTranslation } from 'react-i18next';

import { apiRequest } from '../../api/client';
import type { components as Uw } from '../../api/generated/uw';

/** Underwriting issue types (uw.yaml, typed by the referral decision work). */
export type UwIssueItem = Uw['schemas']['IssueListItem'];
/** uw.Issue.list page (the generated allOf with PageEnvelope types items as unknown[]). */
export interface UwIssueListPage {
  items: UwIssueItem[];
  nextCursor: string | null;
  limit?: number;
}
export type UwIssueStatus = Uw['schemas']['IssueStatusCode'];
export type UwIssueDecideRequest = Uw['schemas']['IssueDecideRequest'];
export type UwIssueDecideResponse = Uw['schemas']['IssueDecideResponse'];

/** The role that decides underwriting referrals (permission uw.Issue.decide and the UW.ISSUE_APPROVAL grant). */
export const underwritingManagerRole = 'Staff.UnderwritingManager';

/** uw.Issue.list for one job (every status, oldest first). */
export function useJobIssues(jobId: string | undefined, enabled: boolean) {
  return useQuery({
    queryKey: ['uw', 'issues', 'job', jobId],
    enabled: enabled && jobId !== undefined,
    queryFn: ({ signal }) =>
      apiRequest<UwIssueListPage>('/api/uw/v1/issues', {
        query: { jobRef: String(jobId), limit: '200' },
        signal,
      }),
  });
}

/** uw.Issue.list without a job: the referral queue (Open issues of the legal entity). */
export function useReferralQueue() {
  return useQuery({
    queryKey: ['uw', 'issues', 'queue'],
    queryFn: ({ signal }) =>
      apiRequest<UwIssueListPage>('/api/uw/v1/issues', {
        query: { status: 'Open', limit: '200' },
        signal,
      }),
  });
}

/** uw.Issue.decide (a command: one Idempotency-Key per user action). */
export function decideIssues(request: UwIssueDecideRequest, idempotencyKey: string) {
  return apiRequest<UwIssueDecideResponse>('/api/uw/v1/issues/decide', {
    method: 'POST',
    body: request,
    idempotencyKey,
  });
}

export function useRefreshIssues() {
  const client = useQueryClient();
  return useCallback(() => client.invalidateQueries({ queryKey: ['uw'] }), [client]);
}

/**
 * Plain-language texts of an issue from the `uw` namespace: the issue type label (never the raw key), the reason
 * and what to change for its rule, the status and the next step. Unknown codes fall back to the server's message.
 */
export function useIssueText() {
  const { t, i18n } = useTranslation('uw');
  return useCallback(
    (
      issue: Pick<UwIssueItem, 'issueType' | 'ruleId' | 'status' | 'severity'> & {
        messageEn?: string;
        messageEl?: string;
      },
    ) => {
      const known = (key: string) => i18n.exists(`uw:${key}`);
      const server = i18n.language === 'en' ? issue.messageEn : issue.messageEl;
      const why = known(`reasons.${issue.ruleId}.why`)
        ? t(`reasons.${issue.ruleId}.why`)
        : (server ?? issue.ruleId);
      const change = known(`reasons.${issue.ruleId}.change`)
        ? t(`reasons.${issue.ruleId}.change`)
        : '';
      const type = known(`issueTypes.${issue.issueType}`)
        ? t(`issueTypes.${issue.issueType}`)
        : issue.issueType;
      const status = known(`statuses.${issue.status}`)
        ? t(`statuses.${issue.status}`)
        : issue.status;
      const stepKey =
        issue.status === 'Open' && issue.severity === 'DECLINE' ? 'OpenDecline' : issue.status;
      const next = known(`nextStep.${stepKey}`) ? t(`nextStep.${stepKey}`, { change }) : '';
      return { type, why, change, status, next };
    },
    [t, i18n],
  );
}
