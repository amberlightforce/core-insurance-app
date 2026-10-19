import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  EmptyState,
  moneyColumn,
  queueColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useReleaseCandidates, type DisbursementView } from './api';
import { DisbursementStatusPill } from './ReleaseParts';

function ReleaseTable({
  rows,
  label,
  emptyTitle,
  emptyBody,
  emptyKind,
}: {
  rows: readonly DisbursementView[];
  label: string;
  emptyTitle: string;
  emptyBody: string;
  emptyKind: 'done' | 'first-use';
}) {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const columns = useMemo<DataColumn<DisbursementView>[]>(
    () => [
      queueColumn<DisbursementView>(
        'release',
        t('releases.columns.release'),
        (d) => ({
          primary: t('releases.queuePrimary'),
          id: d.statementReference ?? d.disbursementNumber ?? d.disbursementId.slice(0, 8),
          fact: d.releaseApproval?.requestedBy ?? '',
        }),
        { size: 320 },
      ),
      moneyColumn<DisbursementView>(
        'amount',
        t('releases.columns.amount'),
        (d) => d.amount.amount,
        {
          currency: 'EUR',
        },
      ),
      textColumn<DisbursementView>('method', t('releases.columns.method'), (d) => d.method ?? ''),
      statusColumn<DisbursementView>(
        'status',
        t('releases.columns.status'),
        (d) => d.status,
        (d) => <DisbursementStatusPill status={d.status} />,
        { size: 200 },
      ),
    ],
    [t],
  );
  return (
    <SimpleTable<DisbursementView>
      aria-label={label}
      columns={columns}
      data={rows}
      getRowId={(d) => d.disbursementId}
      rowVariant="queue"
      onOpen={(d) => {
        void navigate(`/billing/releases/${d.disbursementId}`);
      }}
      emptyState={
        <EmptyState
          kind={emptyKind}
          headingLevel={3}
          headline={emptyTitle}
          description={emptyBody}
        />
      }
    />
  );
}

/** «Εγκρίσεις αποδέσμευσης»: FS_CLEARING disbursements waiting for a second user (D-SL4-12), then all of them. */
export function ReleasesInboxPage() {
  const { t } = useTranslation('billing');
  const query = useReleaseCandidates();
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('releases.title')}
        description={t('releases.subtitle')}
        actions={
          <LinkButton variant="secondary" to="/billing">
            {t('refunds.backToBilling')}
          </LinkButton>
        }
      />
      <QueryView query={query}>
        {(all) => {
          const pending = all.filter((d) => d.status === 'PendingApproval');
          return (
            <>
              <Section title={t('releases.pendingTitle')} family="plum" count={pending.length}>
                <ReleaseTable
                  rows={pending}
                  label={t('releases.pendingTitle')}
                  emptyKind="done"
                  emptyTitle={t('releases.emptyPendingTitle')}
                  emptyBody={t('releases.emptyPendingBody')}
                />
              </Section>
              <Section title={t('releases.allTitle')} family="brand" count={all.length}>
                <ReleaseTable
                  rows={all}
                  label={t('releases.allTitle')}
                  emptyKind="first-use"
                  emptyTitle={t('releases.emptyAllTitle')}
                  emptyBody={t('releases.emptyAllBody')}
                />
              </Section>
            </>
          );
        }}
      </QueryView>
    </div>
  );
}
