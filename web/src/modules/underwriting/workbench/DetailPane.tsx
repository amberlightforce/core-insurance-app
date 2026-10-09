import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  Button,
  cx,
  EmptyState,
  ErrorState,
  KeyValueList,
  LoadingState,
  StatusPill,
  Tooltip,
  useRegionFormat,
  type KeyValueItem,
} from '../../../design-system';
import { formatDateTime, formatMoney } from '../../../format';
import { useIssueText } from '../../quote/uwIssues';
import { PageHeader } from '../../staff/PageHeader';
import { problemOf } from '../../staff/problem';
import { useFormat } from '../../staff/useFormat';
import { useReferral, type ReferralIssue, type ReferralReason, type ReferralView } from '../api';
import { DecisionBar } from './DecisionBar';
import { formatIssueFigure, useIssueTypeLabel } from './helpers';
import styles from './Workbench.module.css';

type IssueStatus = ReferralIssue['issue']['status'];

function IssueStatusPill({ status }: { status: IssueStatus }) {
  switch (status) {
    case 'Open':
      return <StatusPill entity="uwIssue" state="open" size="sm" announceChanges={false} />;
    case 'Approved':
      return <StatusPill entity="uwIssue" state="approved" size="sm" announceChanges={false} />;
    case 'ApprovedWithConditions':
      return (
        <StatusPill
          entity="uwIssue"
          state="approvedWithConditions"
          size="sm"
          announceChanges={false}
        />
      );
    case 'Rejected':
      return <StatusPill entity="uwIssue" state="rejected" size="sm" announceChanges={false} />;
    case 'Invalidated':
      return <StatusPill entity="uwIssue" state="invalidated" size="sm" announceChanges={false} />;
    case 'Closed':
      return <StatusPill entity="uwIssue" state="closed" size="sm" announceChanges={false} />;
  }
}

interface IssueCardProps {
  entry: ReferralIssue;
  /** The fact value and the rule limit the list item recorded for this issue. */
  reason: ReferralReason | undefined;
}

/** One issue (mockup `.issue`): the type with its status, the value against the rule limit, «Γιατί;» and the decision. */
function IssueCard({ entry, reason }: IssueCardProps) {
  const { t, i18n } = useTranslation('underwriting');
  const region = useRegionFormat();
  const describe = useIssueText();
  const typeLabel = useIssueTypeLabel();
  const [showWhy, setShowWhy] = useState(false);
  const { issue } = entry;
  // A driver-rule value arrives as an age band code («FROM_18_TO_20»): shown in words.
  const money = (amount: string) => formatMoney(amount, { currency: 'EUR', region });
  const figure = (value: string) =>
    i18n.exists(`underwriting:detail.ageBand.${value}`)
      ? t(`detail.ageBand.${value}`)
      : formatIssueFigure(issue.issueType, value, t, money);
  const observed = reason?.observed ? figure(reason.observed) : t('issue.noValue');
  const text = describe(issue);
  const limitReason = reason?.limitUnavailableReason;

  return (
    <article className={styles.issue} aria-label={typeLabel(issue.issueType)}>
      <div className={styles.issueTop}>
        <h3 className={styles.issueTitle}>{typeLabel(issue.issueType)}</h3>
        <IssueStatusPill status={issue.status} />
      </div>
      <div className={styles.compare}>
        <span className={styles.muted}>{t('issue.value')}</span>
        <b className={styles.num}>{observed}</b>
        <span className={styles.faint}>{t('issue.limit')}</span>
        {reason?.limit ? (
          <span className={styles.num}>{figure(reason.limit)}</span>
        ) : (
          <Tooltip
            content={t(
              limitReason === 'RULE_DECLARES_NO_LIMIT'
                ? 'issue.limitUnavailable.RULE_DECLARES_NO_LIMIT'
                : 'issue.limitUnavailable.other',
            )}
          >
            <span className={styles.noLimit} tabIndex={0}>
              {t('issue.noValue')}
            </span>
          </Tooltip>
        )}
        <span className={styles.mono}>{issue.ruleId}</span>
        <span className={styles.why}>
          <Button
            variant="ghost"
            size="sm"
            isPressed={showWhy}
            onPress={() => {
              setShowWhy((shown) => !shown);
            }}
          >
            {showWhy ? t('issue.hideWhy') : t('issue.why')}
          </Button>
        </span>
      </div>
      {showWhy ? <p className={styles.explain}>{text.why}</p> : null}
      {issue.decision ? (
        <p className={styles.history}>
          {t('issue.decidedBy', {
            name: issue.decision.decidedBy,
            time: formatDateTime(issue.decision.decidedAt, region),
          })}
          {' · '}
          {t('issue.decisionReason', { reason: issue.decision.reason })}
        </p>
      ) : null}
    </article>
  );
}

function Facts({ referral }: { referral: ReferralView }) {
  const { t } = useTranslation('underwriting');
  const fmt = useFormat();
  const { summary, facts } = referral;

  const items: KeyValueItem[] = [
    {
      id: 'product',
      label: t('detail.kv.product'),
      value: summary.productCode
        ? referral.productVersion
          ? t('detail.kv.productValue', {
              code: summary.productCode,
              version: referral.productVersion,
            })
          : summary.productCode
        : null,
    },
  ];
  if (facts) {
    const vehicle = facts.vehicle;
    const vehicleParts = [
      [vehicle.make, vehicle.model].filter(Boolean).join(' '),
      vehicle.ageYears !== null && vehicle.ageYears !== undefined
        ? t('detail.kv.vehicleAge', { years: vehicle.ageYears })
        : '',
      vehicle.usage ?? '',
      vehicle.engineCapacityCc ? `${String(vehicle.engineCapacityCc)} cc` : '',
    ].filter((part) => part !== '');
    items.push(
      {
        id: 'claims',
        label: t('detail.kv.claims'),
        value:
          facts.driver.claimsLast5Years !== null && facts.driver.claimsLast5Years !== undefined
            ? t('detail.kv.claimsValue', { count: facts.driver.claimsLast5Years })
            : null,
      },
      { id: 'vehicle', label: t('detail.kv.vehicle'), value: vehicleParts.join(' · ') },
      {
        id: 'driver',
        label: t('detail.kv.youngestDriver'),
        value: facts.driver.youngestAgeBand
          ? t(`detail.ageBand.${facts.driver.youngestAgeBand}`)
          : null,
      },
    );
  }
  items.push(
    { id: 'start', label: t('detail.kv.start'), value: fmt.date(summary.effectiveDate) },
    {
      id: 'producer',
      label: t('detail.kv.producer'),
      value: referral.producerCode ?? null,
      kind: 'mono',
    },
  );
  if (facts) {
    items.push({
      id: 'rules',
      label: t('detail.kv.ruleSet'),
      value: `${facts.ruleSetCode} v${facts.ruleSetVersion}`,
      kind: 'mono',
    });
  }

  return (
    <div className={styles.kv}>
      <KeyValueList aria-label={t('detail.kv.label')} items={items} />
      {facts ? null : <p className={styles.caption}>{t('detail.kv.factsMissing')}</p>}
    </div>
  );
}

function ReferralPill({ status }: { status: ReferralView['summary']['referralStatus'] }) {
  if (status === 'Open') return <StatusPill entity="job" state="referred" />;
  if (status === 'Approved') return <StatusPill entity="uwIssue" state="approved" />;
  return <StatusPill entity="uwIssue" state="rejected" />;
}

export interface DetailPaneProps {
  jobRef: string | null;
}

/** The detail sheet (mockup `.detail`): record header, issue cards and the risk facts, then the decision bar. */
export function DetailPane({ jobRef }: DetailPaneProps) {
  const { t } = useTranslation('underwriting');
  const fmt = useFormat();
  const query = useReferral(jobRef);

  if (jobRef === null) {
    return (
      <div className={styles.sheet}>
        <h1 className="ds-visually-hidden">{t('title')}</h1>
        <div className={styles.centre}>
          <EmptyState
            kind="first-use"
            headingLevel={2}
            headline={t('detail.emptyTitle')}
            description={t('detail.emptyBody')}
          />
        </div>
      </div>
    );
  }
  if (query.isPending) {
    return (
      <div className={styles.sheet}>
        <h1 className="ds-visually-hidden">{t('title')}</h1>
        <div className={styles.centre}>
          <LoadingState immediate>{t('detail.loading')}</LoadingState>
        </div>
      </div>
    );
  }
  if (query.isError) {
    const problem = problemOf(query.error);
    return (
      <div className={styles.sheet}>
        <h1 className="ds-visually-hidden">{t('title')}</h1>
        <div className={styles.centre}>
          <ErrorState
            message={
              problem.status === 404 ? t('detail.notFound') : (problem.title ?? t('list.error'))
            }
            {...(problem.traceId ? { correlationId: problem.traceId } : {})}
            {...(problem.status === 404
              ? {}
              : {
                  onRetry: () => {
                    void query.refetch();
                  },
                })}
          />
        </div>
      </div>
    );
  }

  const referral = query.data;
  const { summary } = referral;
  const reasonsByIssue = new Map(summary.reasons.map((reason) => [reason.issueId, reason]));

  return (
    <div className={cx(styles.sheet, styles.detail)}>
      <div className={styles.detailHead}>
        <PageHeader
          overline={t('detail.overline', {
            product: summary.productCode ?? t('detail.unknownProduct'),
          })}
          title={
            summary.customer?.displayName ?? summary.customer?.partyNumber ?? t('queue.noName')
          }
          {...(summary.jobNumber ? { recordId: summary.jobNumber } : {})}
          subtitle={<ReferralPill status={summary.referralStatus} />}
          facts={[
            {
              id: 'premium',
              label: t('detail.facts.premium'),
              value: fmt.money(summary.premiumTotal),
            },
            { id: 'start', label: t('detail.facts.start'), value: fmt.date(summary.effectiveDate) },
            ...(referral.producerCode
              ? [
                  {
                    id: 'producer',
                    label: t('detail.facts.producer'),
                    value: referral.producerCode,
                  },
                ]
              : []),
          ]}
        />
      </div>
      <div className={styles.detailBody}>
        <div className={styles.column}>
          {referral.issues.map((entry) => (
            <IssueCard
              key={entry.issue.id}
              entry={entry}
              reason={reasonsByIssue.get(entry.issue.id)}
            />
          ))}
        </div>
        <div className={styles.column}>
          <Facts referral={referral} />
        </div>
      </div>
      <DecisionBar referral={referral} />
    </div>
  );
}
