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

type CountText = (key: string, options: { count: number }) => string;

/** «7 ώρ.», «2 ημ.», «5 λ.»: how long a referral has been waiting, compact and never cut by the column. */
export function waitingSince(raisedAt: string, t: CountText, now: Date = new Date()): string {
  const minutes = Math.max(0, Math.floor((now.getTime() - new Date(raisedAt).getTime()) / 60_000));
  if (minutes < 60) return t('underwriting:queue.waiting.minutes', { count: minutes });
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return t('underwriting:queue.waiting.hours', { count: hours });
  return t('underwriting:queue.waiting.days', { count: Math.floor(hours / 24) });
}

/**
 * The value or limit of an issue with its unit, by issue type: vehicle age in years, vehicle value in money, claims as
 * a count. Anything else (an age band is written in words by the caller) shows as the server gave it.
 */
export function formatIssueFigure(
  issueType: string,
  value: string,
  t: CountText,
  money: (amount: string) => string,
): string {
  const figure = Number(value);
  if (value.trim() === '' || !Number.isFinite(figure)) return value;
  switch (issueType) {
    case 'VEHICLE_AGE_REFERRAL':
      return t('underwriting:issue.units.years', { count: figure });
    case 'VEHICLE_VALUE_REFERRAL':
      return money(value);
    case 'CLAIMS_HISTORY':
      return t('underwriting:issue.units.claims', { count: figure });
    default:
      return value;
  }
}
