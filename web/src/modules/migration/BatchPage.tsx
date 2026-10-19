import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import {
  EmptyState,
  cx,
  Select,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import staff from '../staff/staff.module.css';
import { BatchActions } from './BatchActions';
import { outcomeCodes, remember, useBatch, type Outcome } from './api';
import styles from './Migration.module.css';
import { BatchStatusPill, OutcomePill } from './StatusPills';

const all = 'ALL';

/** Batch detail: status and counts, the decision bar, and the per-item outcomes (dry-run results included). */
export function BatchPage() {
  const { t } = useTranslation('migration');
  const { batchId = '' } = useParams();
  const query = useBatch(batchId);
  const [filter, setFilter] = useState<string>(all);
  const batchName = query.data?.batch.name;

  useEffect(() => {
    if (batchName) remember({ batchId, name: batchName });
  }, [batchId, batchName]);

  const columns = useMemo<DataColumn<Outcome>[]>(
    () => [
      textColumn<Outcome>('seq', t('outcomes.columns.seq'), (o) => String(o.seq), { size: 70 }),
      textColumn<Outcome>('module', t('outcomes.columns.module'), (o) => o.module, { size: 90 }),
      textColumn<Outcome>('type', t('outcomes.columns.type'), (o) => o.objectType, { size: 150 }),
      identifierColumn<Outcome>('legacy', t('outcomes.columns.legacy'), (o) => o.legacyId, {
        size: 200,
      }),
      statusColumn<Outcome>(
        'outcome',
        t('outcomes.columns.outcome'),
        (o) => o.outcome,
        (o) => <OutcomePill outcome={o.outcome} />,
        { size: 190 },
      ),
      identifierColumn<Outcome>('core', t('outcomes.columns.core'), (o) => o.coreId ?? '—', {
        size: 280,
      }),
      textColumn<Outcome>(
        'message',
        t('outcomes.columns.message'),
        (o) => [o.errorCode, o.message].filter(Boolean).join(' · ') || '—',
        { size: 360 },
      ),
    ],
    [t],
  );

  return (
    <div className={staff.page}>
      <QueryView query={query} notFoundMessage={t('batch.notFound')}>
        {({ batch, outcomes }) => {
          const rows = filter === all ? outcomes : outcomes.filter((o) => o.outcome === filter);
          return (
            <>
              <PageHeader
                overline={t('overline')}
                title={batch.name}
                recordId={batch.batchId}
                subtitle={<BatchStatusPill status={batch.status} />}
                actions={
                  <LinkButton variant="secondary" to="/admin/migration">
                    {t('batch.back')}
                  </LinkButton>
                }
                facts={[
                  { id: 'items', label: t('batch.items'), value: batch.itemCount },
                  {
                    id: 'dry',
                    label: t('batch.dryRun'),
                    value: t(batch.dryRunDone ? 'batch.dryRunYes' : 'batch.dryRunNo'),
                  },
                  {
                    id: 'baseline',
                    label: t('batch.baseline'),
                    value: (
                      <span className={cx('ds-mono', styles.hash)}>
                        {batch.baselineHash?.slice(0, 16) ?? '—'}
                      </span>
                    ),
                  },
                ]}
              />
              <Section title={t('batch.decision')}>
                <BatchActions batch={batch} />
              </Section>
              <Section title={t('batch.counts')}>
                <ul className={styles.counts} aria-label={t('batch.counts')}>
                  {outcomeCodes
                    .filter((code) => (batch.counts[code] ?? 0) > 0)
                    .map((code) => (
                      <li key={code}>
                        <OutcomePill outcome={code} /> <strong>{batch.counts[code]}</strong>
                      </li>
                    ))}
                </ul>
              </Section>
              <Section title={t('outcomes.title')} count={rows.length}>
                <div className={styles.inline}>
                  <Select
                    label={t('outcomes.filter')}
                    options={[
                      { id: all, label: t('outcomes.all') },
                      ...outcomeCodes.map((c) => ({ id: c, label: t(`outcome.${c}`) })),
                    ]}
                    value={filter}
                    onChange={(value) => {
                      setFilter(value ?? all);
                    }}
                  />
                </div>
                <SimpleTable<Outcome>
                  aria-label={t('outcomes.title')}
                  columns={columns}
                  data={rows}
                  getRowId={(o) => String(o.seq)}
                  getRowLabel={(o) => o.legacyId}
                  emptyState={
                    <EmptyState
                      kind="filtered"
                      headingLevel={3}
                      filters={[{ label: t('outcomes.filter'), value: t(`outcome.${filter}`) }]}
                      onClearFilters={() => {
                        setFilter(all);
                      }}
                    />
                  }
                />
              </Section>
            </>
          );
        }}
      </QueryView>
    </div>
  );
}
