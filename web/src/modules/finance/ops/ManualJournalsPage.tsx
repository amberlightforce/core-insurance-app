import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  EmptyState,
  dateColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useManualJournals } from './api';
import { lineTotals } from './journalMath';
import { ManualJournalStatusPill } from './ManualJournalParts';
import type { ManualJournal } from './types';

/** Manual journals (fin.ManualJournal.list): who proposed what, and what still waits for a second user. */
export function ManualJournalsPage() {
  const { t } = useTranslation('finance');
  const navigate = useNavigate();
  const query = useManualJournals();

  const columns: DataColumn<ManualJournal>[] = [
    textColumn<ManualJournal>(
      'number',
      t('ops.manual.number'),
      (j) => j.number ?? j.id.slice(0, 8),
      {
        size: 140,
      },
    ),
    textColumn<ManualJournal>('description', t('ops.manual.description'), (j) => j.description, {
      size: 320,
    }),
    dateColumn<ManualJournal>('date', t('ops.manual.accountingDate'), (j) => j.accountingDate),
    moneyColumn<ManualJournal>('amount', t('ops.manual.amount'), (j) => lineTotals(j.lines).debit, {
      currency: 'EUR',
    }),
    statusColumn<ManualJournal>(
      'status',
      t('ops.manual.statusLabel'),
      (j) => j.status,
      (j) => <ManualJournalStatusPill status={j.status} />,
    ),
  ];

  return (
    <div className={styles.page}>
      <PageHeader
        title={t('ops.manual.title')}
        overline={t('overline')}
        description={t('ops.manual.lead')}
        variant="landing"
        actions={
          <>
            <LinkButton variant="primary" to="/finance/manual-journals/new">
              {t('ops.manual.new')}
            </LinkButton>
            <LinkButton variant="secondary" to="/finance">
              {t('ops.back')}
            </LinkButton>
          </>
        }
      />
      <Section title={t('ops.manual.title')}>
        <QueryView query={query}>
          {(page) =>
            page.items.length === 0 ? (
              <EmptyState
                kind="first-use"
                illustration={<></>}
                headingLevel={3}
                headline={t('ops.manual.emptyTitle')}
                description={t('ops.manual.emptyBody')}
              />
            ) : (
              <SimpleTable<ManualJournal>
                aria-label={t('ops.manual.title')}
                columns={columns}
                data={page.items.map((i) => i.manualJournal)}
                getRowId={(j) => j.id}
                getRowLabel={(j) => j.number ?? j.description}
                onOpen={(j) => {
                  void navigate(`/finance/manual-journals/${encodeURIComponent(j.id)}`);
                }}
              />
            )
          }
        </QueryView>
      </Section>
    </div>
  );
}
