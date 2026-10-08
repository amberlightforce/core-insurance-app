import { useQueries } from '@tanstack/react-query';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import type { ApprovalView, TransactionSetView } from '../../api/types';
import {
  EmptyState,
  dateColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { fetchSet, setIdOfSubject, useApprovals } from './api';
import { approvalTypeKey } from './approvalFormat';

function setsById(results: { data?: { set: TransactionSetView } | undefined }[]) {
  return new Map(results.flatMap((q) => (q.data ? [[q.data.set.setId, q.data.set] as const] : [])));
}

/** Approvals inbox: requests waiting for a decision (plt.Approval.list, status PendingApproval). */
export function ApprovalsInboxPage() {
  const { t } = useTranslation('claims');
  const navigate = useNavigate();
  const query = useApprovals('PendingApproval');

  const all = useMemo<ApprovalView[]>(
    () =>
      (query.data?.items as { request: ApprovalView }[] | undefined)?.map((i) => i.request) ?? [],
    [query.data],
  );

  // Reserve approvals point at their transaction set: it gives the claim to open and shows stale leftovers.
  const setIds = useMemo(
    () => [...new Set(all.map((r) => setIdOfSubject(r.objectRef)).filter((id) => id !== null))],
    [all],
  );
  const sets = useQueries({
    queries: setIds.map((id) => ({
      queryKey: ['clm', 'set', id],
      queryFn: ({ signal }: { signal: AbortSignal }) => fetchSet(id, signal),
    })),
    combine: setsById,
  });
  const rows = useMemo(
    () =>
      all.filter((r) => {
        const id = setIdOfSubject(r.objectRef);
        return !(id && sets.get(id)?.status === 'REJECTED');
      }),
    [all, sets],
  );
  const hidden = all.length - rows.length;

  const columns = useMemo<DataColumn<ApprovalView>[]>(
    () => [
      textColumn<ApprovalView>('type', t('approvals.columns.type'), (r) =>
        t(approvalTypeKey(r.type), { defaultValue: r.type }),
      ),
      textColumn<ApprovalView>(
        'subject',
        t('approvals.columns.subject'),
        (r) => `${r.objectRef.module} · ${r.objectRef.type}`,
      ),
      moneyColumn<ApprovalView>(
        'amount',
        t('approvals.columns.amount'),
        (r) => r.authority.amount?.amount ?? null,
        { currency: 'EUR' },
      ),
      textColumn<ApprovalView>('maker', t('approvals.columns.maker'), (r) => r.maker.id),
      dateColumn<ApprovalView>(
        'requested',
        t('approvals.columns.requestedAt'),
        (r) => r.requestedAt,
      ),
      textColumn<ApprovalView>('reason', t('approvals.columns.reason'), (r) => r.reason ?? null),
      statusColumn<ApprovalView>(
        'claim',
        t('approvals.columns.claim'),
        (r) => {
          const id = setIdOfSubject(r.objectRef);
          return id ? (sets.get(id)?.claimId ?? '') : '';
        },
        (r) => {
          const id = setIdOfSubject(r.objectRef);
          const claimId = id ? sets.get(id)?.claimId : undefined;
          return claimId ? (
            <LinkButton to={`/claims/${claimId}?tab=financials`}>
              {t('approvals.openClaim')}
            </LinkButton>
          ) : (
            <span className="ds-caption">{t('approvals.noClaimLink')}</span>
          );
        },
      ),
    ],
    [t, sets],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        overline={t('overline')}
        title={t('approvals.title')}
        subtitle={<span className="ds-caption">{t('approvals.subtitle')}</span>}
        actions={
          <LinkButton variant="secondary" to="/claims">
            {t('approvals.backToClaims')}
          </LinkButton>
        }
      />
      <Section title={t('approvals.pending')}>
        <QueryView query={query}>
          {() => (
            <div className={styles.stack}>
              {hidden > 0 ? (
                <p className={styles.muted}>{t('approvals.hiddenStale', { count: hidden })}</p>
              ) : null}
              <SimpleTable<ApprovalView>
                aria-label={t('approvals.pending')}
                columns={columns}
                data={rows}
                getRowId={(r) => r.requestId}
                onOpen={(r) => {
                  void navigate(`/claims/approvals/${r.requestId}`);
                }}
                emptyState={
                  <EmptyState
                    kind="done"
                    headingLevel={3}
                    headline={t('approvals.emptyTitle')}
                    description={t('approvals.emptyBody')}
                  />
                }
              />
            </div>
          )}
        </QueryView>
      </Section>
    </div>
  );
}
