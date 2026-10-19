import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  EmptyState,
  Select,
  dateColumn,
  identifierColumn,
  moneyColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useDelinquencies, type DelinquencyCase, type DelinquencyStatus } from './api';
import { daysRemaining } from './roles';

const ALL = 'ALL';
export const delinquencyStatuses: DelinquencyStatus[] = [
  'OPEN',
  'NOTICE_SENT',
  'NOTICE_RETURNED',
  'ARRANGED',
  'ON_HOLD',
  'CANCELLATION_PENDING',
  'RESOLVED',
];

/** Delinquency «Καθυστερήσεις»: non-payment cases with their waiting-period clock. */
export function DelinquencyListPage() {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const [status, setStatus] = useState<string>(ALL);
  const query = useDelinquencies(status !== ALL ? (status as DelinquencyStatus) : undefined);

  const columns = useMemo<DataColumn<DelinquencyCase>[]>(
    () => [
      identifierColumn<DelinquencyCase>('account', t('delinquency.columns.account'), (r) =>
        r.billingAccountId.slice(0, 8),
      ),
      textColumn<DelinquencyCase>('status', t('delinquency.columns.status'), (r) =>
        t(`delinquency.status.${r.status}`),
      ),
      moneyColumn<DelinquencyCase>(
        'open',
        t('delinquency.columns.open'),
        (r) => r.openAmount.amount,
        { currency: 'EUR' },
      ),
      dateColumn<DelinquencyCase>('started', t('delinquency.columns.started'), (r) => r.startedAt),
      textColumn<DelinquencyCase>('clock', t('delinquency.columns.clock'), (r) => {
        const d = daysRemaining(r.clockDueAt);
        return d === null ? t('delinquency.noClock') : t('delinquency.daysLeft', { count: d });
      }),
    ],
    [t],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('delinquency.title')}
        description={t('delinquency.subtitle')}
        actions={
          <LinkButton variant="secondary" to="/billing">
            {t('refunds.backToBilling')}
          </LinkButton>
        }
      />
      <Section title={t('delinquency.listTitle')} family="teal" count={query.data?.items.length}>
        <div className={styles.grid}>
          <Select
            label={t('delinquency.filters.status')}
            id="delinquency-status"
            options={[
              { id: ALL, label: t('delinquency.filters.all') },
              ...delinquencyStatuses.map((s) => ({ id: s, label: t(`delinquency.status.${s}`) })),
            ]}
            value={status}
            onChange={(v) => {
              setStatus(v ?? ALL);
            }}
          />
        </div>
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<DelinquencyCase>
              aria-label={t('delinquency.listTitle')}
              columns={columns}
              data={page.items}
              getRowId={(r) => r.delinquencyId}
              onOpen={(r) => {
                void navigate(`/billing/delinquency/${r.delinquencyId}`);
              }}
              emptyState={
                <EmptyState
                  kind="first-use"
                  headingLevel={3}
                  headline={t('delinquency.emptyTitle')}
                  description={t('delinquency.emptyBody')}
                />
              }
            />
          )}
        </QueryView>
      </Section>
    </div>
  );
}
