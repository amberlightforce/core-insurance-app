import { useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-aria-components';

import type { ApprovalView } from '../../api/types';
import { EmptyState, type StatusFamily } from '../../design-system';
import { claimsRoles, underwritingRoles, withdrawalRoles } from '../../app-shell/navigation';
import { useDevSession } from '../../dev-auth/devAuth';
import { usePendingApprovals } from '../home/api';
import { refundViewRoles } from '../billing/refunds/roles';
import { useRefunds, refundsOf } from '../billing/refunds/api';
import { approvalTypeKey } from '../claims/approvalFormat';
import { dsarRoles } from '../compliance/dsar/access';
import { useDsarList } from '../compliance/dsar/api';
import { useWithdrawals, type WithdrawalListItem } from '../channels/withdrawal/api';
import { useExceptions } from '../policy/packRollback/api';
import { useKindLabel } from '../policy/packRollback/kinds';
import { useReferralList } from '../underwriting/api';
import { PageHeader, Section } from '../staff/PageHeader';
import { LinkButton } from '../staff/LinkButton';
import { QueryView } from '../staff/QueryView';
import staff from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import styles from './Work.module.css';

/** How many items a section lists before «see all». */
const topCount = 5;

export interface WorkItem {
  id: string;
  to: string;
  label: ReactNode;
  meta?: string;
}

interface QueueQuery {
  isPending: boolean;
  isError: boolean;
  error: unknown;
  refetch: () => unknown;
}

interface QueueSectionProps {
  title: string;
  family: StatusFamily;
  query: QueueQuery;
  /** The pending items; read only once the query succeeded. */
  items: WorkItem[];
  seeAllTo: string;
  /** Overrides the number of items when the server knows the full count. */
  count?: number | undefined;
}

/**
 * One queue: its own loading, error (with retry) and empty states, so one failing API never breaks the page. The
 * items link to the existing detail pages.
 */
function QueueSection({ title, family, query, items, seeAllTo, count }: QueueSectionProps) {
  const { t } = useTranslation('work');
  const ready = !query.isPending && !query.isError;
  const total = count ?? items.length;
  const adapter = {
    data: ready ? items : undefined,
    isPending: query.isPending,
    isError: query.isError,
    error: query.error,
    refetch: query.refetch,
  };
  return (
    <Section title={title} family={family} {...(ready ? { count: total } : {})}>
      <QueryView query={adapter}>
        {(rows) =>
          rows.length === 0 ? (
            <EmptyState kind="done" headingLevel={3} headline={t('empty')} />
          ) : (
            <>
              <ul className={styles.list}>
                {rows.slice(0, topCount).map((item) => (
                  <li key={item.id} className={styles.item}>
                    <Link href={item.to}>{item.label}</Link>
                    {item.meta ? <span className={styles.itemMeta}>{item.meta}</span> : null}
                  </li>
                ))}
              </ul>
              <div className={styles.footer}>
                <span className={staff.muted}>
                  {total > topCount ? t('more', { count: total - topCount }) : ''}
                </span>
                <LinkButton to={seeAllTo}>{t('seeAll')}</LinkButton>
              </div>
            </>
          )
        }
      </QueryView>
    </Section>
  );
}

function ReferralsSection() {
  const { t } = useTranslation('work');
  const fmt = useFormat();
  const { query, items, counts } = useReferralList('OPEN');
  const rows = useMemo<WorkItem[]>(
    () =>
      items.map((r) => ({
        id: r.jobRef,
        to: `/underwriting?queue=OPEN&job=${encodeURIComponent(r.jobRef)}`,
        label: r.jobNumber ?? r.jobRef,
        meta: t('item.raised', { date: fmt.date(r.raisedAt) }),
      })),
    [items, fmt, t],
  );
  return (
    <QueueSection
      title={t('sections.referrals')}
      family="warning"
      query={query}
      items={rows}
      seeAllTo="/underwriting"
      count={counts?.open}
    />
  );
}

function ApprovalsSection() {
  const { t } = useTranslation(['work', 'claims']);
  const fmt = useFormat();
  const query = usePendingApprovals(true);
  const rows = useMemo<WorkItem[]>(
    () =>
      ((query.data?.items as { request: ApprovalView }[] | undefined) ?? []).map(({ request }) => ({
        id: request.requestId,
        to: `/claims/approvals/${request.requestId}`,
        label: t(approvalTypeKey(request.type), { ns: 'claims', defaultValue: request.type }),
        meta: t('work:item.requested', { date: fmt.date(request.requestedAt) }),
      })),
    [query.data, fmt, t],
  );
  return (
    <QueueSection
      title={t('work:sections.approvals')}
      family="plum"
      query={query}
      items={rows}
      seeAllTo="/claims/approvals"
    />
  );
}

function RefundsSection() {
  const { t } = useTranslation('work');
  const fmt = useFormat();
  const query = useRefunds({ state: 'PENDING_APPROVAL' });
  const rows = useMemo<WorkItem[]>(
    () =>
      refundsOf(query.data).map((r) => ({
        id: r.refundId,
        to: `/billing/refunds/${r.refundId}`,
        label: t('item.refund', { amount: fmt.money(r.amount) }),
        meta: t('item.requested', { date: fmt.date(r.proposedAt) }),
      })),
    [query.data, fmt, t],
  );
  return (
    <QueueSection
      title={t('sections.refunds')}
      family="plum"
      query={query}
      items={rows}
      seeAllTo="/billing/refunds"
    />
  );
}

function WithdrawalsSection() {
  const { t } = useTranslation('work');
  const fmt = useFormat();
  const query = useWithdrawals('');
  const rows = useMemo<WorkItem[]>(
    () =>
      ((query.data?.items ?? []) as WithdrawalListItem[])
        // DECLARED still waits for the customer's confirmation; staff work the confirmed ones.
        .filter((r) => r.status === 'CONFIRMED' || r.status === 'SUBMITTED')
        .map((r) => ({
          id: r.withdrawalRequestId,
          to: `/channels/withdrawals/${r.withdrawalRequestId}`,
          label: t('item.withdrawal', { number: r.requestNumber }),
          meta: t('item.declared', { date: fmt.date(r.declaredAt) }),
        })),
    [query.data, fmt, t],
  );
  return (
    <QueueSection
      title={t('sections.withdrawals')}
      family="warning"
      query={query}
      items={rows}
      seeAllTo="/channels/withdrawals"
    />
  );
}

function DsarSection() {
  const { t } = useTranslation('work');
  const fmt = useFormat();
  const query = useDsarList();
  const rows = useMemo<WorkItem[]>(
    () =>
      (query.data?.items ?? [])
        .filter(({ request }) => request.status !== 'COMPLETED' && request.status !== 'REFUSED')
        .sort((a, b) => a.request.deadline.localeCompare(b.request.deadline))
        .map(({ request }) => ({
          id: request.dsarId,
          to: `/compliance/dsar/${request.dsarId}`,
          label: t(`item.dsarType.${request.requestType}`, { defaultValue: request.requestType }),
          meta: t('item.deadline', { date: fmt.date(request.deadline) }),
        })),
    [query.data, fmt, t],
  );
  return (
    <QueueSection
      title={t('sections.dsar')}
      family="danger"
      query={query}
      items={rows}
      seeAllTo="/compliance/dsar"
    />
  );
}

function PackRollbackSection() {
  const { t } = useTranslation('work');
  const fmt = useFormat();
  const kindLabel = useKindLabel();
  const query = useExceptions('OPEN');
  const rows = useMemo<WorkItem[]>(
    () =>
      (query.data?.items ?? []).map((e) => ({
        id: e.exceptionId,
        to: `/policies/pack-rollback/${e.exceptionId}`,
        label: `${e.policyNumber} · ${kindLabel(e.transactionKind)}`,
        meta: fmt.date(e.identifiedAt),
      })),
    [query.data, fmt, kindLabel],
  );
  return (
    <QueueSection
      title={t('sections.packRollback')}
      family="warning"
      query={query}
      items={rows}
      seeAllTo="/policies/pack-rollback"
    />
  );
}

const anyOf = (roles: readonly string[], allowed: readonly string[]) =>
  roles.some((role) => allowed.includes(role));

/**
 * «Η δουλειά μου» (/work): one section per work queue the signed-in user's roles allow, built from the lists the
 * module screens already read. Roles only decide which sections show; the API still enforces permissions.
 * TODO(hardening): replace this frontend aggregation with the WRK task module (assignment, due dates, unified inbox).
 */
export function WorkPage() {
  const { t } = useTranslation('work');
  const roles = useDevSession()?.user.roles ?? [];

  const sections: ReactNode[] = [];
  if (anyOf(roles, underwritingRoles)) {
    sections.push(<ReferralsSection key="referrals" />);
    sections.push(<PackRollbackSection key="packRollback" />);
  }
  if (anyOf(roles, claimsRoles)) sections.push(<ApprovalsSection key="approvals" />);
  if (anyOf(roles, refundViewRoles)) sections.push(<RefundsSection key="refunds" />);
  if (anyOf(roles, withdrawalRoles)) sections.push(<WithdrawalsSection key="withdrawals" />);
  if (anyOf(roles, dsarRoles)) sections.push(<DsarSection key="dsar" />);

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('title')}
        description={t('lead')}
      />
      {sections.length === 0 ? (
        <EmptyState
          kind="first-use"
          headingLevel={2}
          headline={t('noQueues.title')}
          description={t('noQueues.body')}
        />
      ) : (
        <div className={styles.grid}>{sections}</div>
      )}
    </div>
  );
}
