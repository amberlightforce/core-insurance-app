import { useCallback } from 'react';
import { useTranslation } from 'react-i18next';

import type { AuthorityPreview, ReferralIssue, ReferralView } from '../api';

/** Staff roles that may open the workbench (permission uw.Referral.list; the API enforces it on every call). */
export const workbenchRoles = ['Staff.UnderwritingManager', 'Platform.Admin'] as const;

/** The issues of a referral that wait for a decision. */
export function openIssuesOf(referral: ReferralView): ReferralIssue[] {
  return referral.issues.filter((entry) => entry.issue.status === 'Open');
}

const outcomeRank: Record<AuthorityPreview['outcome'], number> = { ALLOW: 0, REFER: 1, DENY: 2 };

/**
 * The open issue whose authority preview is the most restrictive (DENY over REFER over ALLOW): the bar shows that
 * one, because the referral can be decided only when every open issue can.
 */
export function mostRestrictive(open: readonly ReferralIssue[]): ReferralIssue | undefined {
  let worst: ReferralIssue | undefined;
  for (const entry of open) {
    if (
      !worst ||
      outcomeRank[entry.decidability.authority.outcome] >
        outcomeRank[worst.decidability.authority.outcome]
    ) {
      worst = entry;
    }
  }
  return worst;
}

/** The issue type's plain label (shared `uw` namespace); an unknown type shows the code rather than nothing. */
export function useIssueTypeLabel() {
  const { t, i18n } = useTranslation('uw');
  return useCallback(
    (issueType: string) =>
      i18n.exists(`uw:issueTypes.${issueType}`) ? t(`issueTypes.${issueType}`) : issueType,
    [t, i18n],
  );
}
