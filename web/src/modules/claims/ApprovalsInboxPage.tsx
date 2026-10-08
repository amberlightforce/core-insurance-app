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
import { approvalClaimId, approvalSetId, fetchSet, useApprovals } from './api';
import { approvalTypeKey } from './approvalFormat';

function setsById(results: { data?: { set: TransactionSetView } | undefined }[]) {
  return new Map(results.flatMap((q) => (q.data ? [[q.data.set.setId, q.data.set] as const] : [])));
}

interface Row {
  request: ApprovalView;
  claimId: string | null;
  rejected: boolean;
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
    () => [...new Set(all.map((r) => approvalSetId(r)).filter((id) => id !== null))],
    [all],
  );
  const sets = useQueries({
    queries: setIds.map((id) => ({
      queryKey: ['clm', 'set', id],
      queryFn: ({ signal }: { signal: AbortSignal }) => fetchSet(id, signal),
    })),
    combine: setsById,
  });
  // Rows carry the claim of their set so the cells depend on the row only (never on a closure).
  const rows = useMemo<Row[]>(
    () =>
      all
        .map((request) => {
          const id = approvalSetId(request);
          const set = id ? sets.get(id) : undefined;
          return {
            request,
            claimId: approvalClaimId(request) ?? set?.claimId ?? null,
            rejected: set?.status === 'REJECTED',
          };
        })
        .filter((row) => !row.rejected),
    [all, sets],
  );
  const hidden = all.length - rows.length;

  const columns = useMemo<DataColumn<Row>[]>(
    () => [
      textColumn<Row>('type', t('approvals.columns.type'), (r) =>
        t(approvalTypeKey(r.request.type), { defaultValue: r.request.type }),
      ),
      textColumn<Row>(
        'subject',
        t('approvals.columns.subject'),
        (r) => `${r.request.objectRef.module} · ${r.request.objectRef.type}`,
      ),
      moneyColumn<Row>(
        'amount',
        t('approvals.columns.amount'),
        (r) => r.request.authority.amount?.amount ?? null,
        { currency: 'EUR' },
      ),
      textColumn<Row>('maker', t('approvals.columns.maker'), (r) => r.request.maker.id),
      dateColumn<Row>(
        'requested',
        t('approvals.columns.requestedAt'),
        (r) => r.request.requestedAt,
      ),
      textColumn<Row>('reason', t('approvals.columns.reason'), (r) => r.request.reason ?? null),
      statusColumn<Row>(
        'claim',
        t('approvals.columns.claim'),
        (r) => r.claimId ?? '',
        (r) =>
          r.claimId ? (
            <LinkButton to={`/claims/${r.claimId}?tab=financials`}>
              {t('approvals.openClaim')}
            </LinkButton>
          ) : (
            <span className="ds-caption">{t('approvals.noClaimLink')}</span>
          ),
      ),
    ],
    [t],
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
              <SimpleTable<Row>
                aria-label={t('approvals.pending')}
                columns={columns}
                data={rows}
                getRowId={(r) => r.request.requestId}
                onOpen={(r) => {
                  void navigate(`/claims/approvals/${r.request.requestId}`);
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
