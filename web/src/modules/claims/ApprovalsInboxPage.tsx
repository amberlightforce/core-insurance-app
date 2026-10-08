import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import type { ApprovalView } from '../../api/types';
import {
  EmptyState,
  dateColumn,
  moneyColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useApprovals } from './api';
import { approvalTypeKey } from './approvalFormat';

/** Approvals inbox: requests waiting for a decision (plt.Approval.list, status PendingApproval). */
export function ApprovalsInboxPage() {
  const { t } = useTranslation('claims');
  const navigate = useNavigate();
  const query = useApprovals('PendingApproval');

  const rows = useMemo<ApprovalView[]>(
    () =>
      (query.data?.items as { request: ApprovalView }[] | undefined)?.map((i) => i.request) ?? [],
    [query.data],
  );

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
          )}
        </QueryView>
      </Section>
    </div>
  );
}
