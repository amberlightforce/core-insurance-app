import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import { EmptyState, statusColumn, textColumn, type DataColumn } from '../../design-system';
import { useReceivables } from '../billing/receivables/api';
import { useDelinquencies } from '../billing/delinquency/api';
import { NavList } from '../staff/NavList';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { groupByStatus, sumByCurrency, type StatusRow } from './summarise';

/** Where the detailed operational views live; each target enforces its own role (a missing role shows «No permission»). */
const views = [
  { id: 'journals', to: '/finance' },
  { id: 'receivables', to: '/billing/receivables' },
  { id: 'delinquency', to: '/billing/delinquency' },
  { id: 'releases', to: '/billing/releases' },
  { id: 'refunds', to: '/billing/refunds' },
  { id: 'approvals', to: '/claims/approvals' },
  { id: 'fsStatements', to: '/claims/fs-statements' },
  { id: 'compliance', to: '/compliance' },
] as const;

function MoneyList({ rows }: { rows: StatusRow }) {
  const fmt = useFormat();
  const totals = sumByCurrency(rows.open);
  const entries = Object.entries(totals);
  return (
    <span className={styles.money}>
      {entries.length === 0
        ? '—'
        : entries.map(([currency, amount]) => fmt.money({ amount, currency })).join(' · ')}
    </span>
  );
}

function StatusTable({
  label,
  rows,
  statusLabel,
  amountLabel,
}: {
  label: string;
  rows: StatusRow[];
  statusLabel: (status: string) => string;
  amountLabel: string;
}) {
  const { t } = useTranslation('reports');
  const columns = useMemo<DataColumn<StatusRow>[]>(
    () => [
      textColumn<StatusRow>('status', t('columns.status'), (r) => statusLabel(r.status)),
      textColumn<StatusRow>('count', t('columns.count'), (r) => String(r.count)),
      statusColumn<StatusRow>(
        'amount',
        amountLabel,
        (r) => r.status,
        (r) => <MoneyList rows={r} />,
      ),
    ],
    [t, statusLabel, amountLabel],
  );
  return (
    <SimpleTable<StatusRow>
      aria-label={label}
      columns={columns}
      data={rows}
      getRowId={(r) => r.status}
      getRowLabel={(r) => statusLabel(r.status)}
      emptyState={
        <EmptyState
          kind="first-use"
          illustration={<></>}
          headingLevel={3}
          headline={t('emptyTitle')}
          description={t('emptyBody')}
        />
      }
    />
  );
}

function ReceivablesSummary() {
  const { t } = useTranslation('reports');
  const query = useReceivables({});
  return (
    <QueryView query={query}>
      {(page) => {
        const items = page.items;
        const rows = groupByStatus(
          items,
          (r) => r.status,
          (r) => r.openAmount,
        );
        return (
          <>
            <StatusTable
              label={t('receivables.title')}
              rows={rows}
              statusLabel={(s) => t(`receivables.status.${s}`, { defaultValue: s })}
              amountLabel={t('receivables.open')}
            />
            {page.nextCursor ? (
              <p className={styles.muted}>{t('partial', { count: items.length })}</p>
            ) : null}
          </>
        );
      }}
    </QueryView>
  );
}

function DelinquencySummary() {
  const { t } = useTranslation('reports');
  const query = useDelinquencies();
  return (
    <QueryView query={query}>
      {(cases) => {
        const rows = groupByStatus(
          cases,
          (c) => c.state,
          (c) => c.overdueAmount,
        );
        return (
          <StatusTable
            label={t('delinquency.title')}
            rows={rows}
            statusLabel={(s) => t(`delinquency.state.${s}`, { defaultValue: s })}
            amountLabel={t('delinquency.overdue')}
          />
        );
      }}
    </QueryView>
  );
}

/** Reports landing: live summaries from the billing reads that exist, plus the way into the detailed operational views. */
export function ReportsPage() {
  const { t } = useTranslation('reports');
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('title')}
        overline={t('overline')}
        description={t('lead')}
      />
      <div className={styles.grid}>
        <Section title={t('receivables.title')} family="teal" meta={t('receivables.source')}>
          <ReceivablesSummary />
        </Section>
        <Section title={t('delinquency.title')} family="warning" meta={t('delinquency.source')}>
          <DelinquencySummary />
        </Section>
      </div>
      <Section title={t('views.title')} family="brand">
        <NavList
          aria-label={t('views.title')}
          items={views.map((v) => ({ id: v.id, label: t(`views.${v.id}.name`), to: v.to }))}
        />
        <p className={styles.muted}>{t('views.gap')}</p>
      </Section>
    </div>
  );
}
