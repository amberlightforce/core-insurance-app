import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import type { JournalView } from '../../api/types';
import {
  Button,
  EmptyState,
  KeyValueList,
  StatusPill,
  moneyColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { useJournals } from './api';
import { journalTotals } from './journal';

type Line = JournalView['lines'][number];

function lineColumns(
  t: (key: string) => string,
  lang: string,
  currency: string,
): DataColumn<Line>[] {
  return [
    textColumn<Line>('lineNo', t('lines.no'), (l) => String(l.lineNo), { size: 70 }),
    textColumn<Line>(
      'account',
      t('lines.account'),
      (l) => `${l.account} · ${lang === 'en' ? l.accountName.en : l.accountName.el}`,
      { size: 320 },
    ),
    moneyColumn<Line>(
      'debit',
      t('lines.debit'),
      (l) => (l.side === 'DEBIT' ? l.amount.amount : null),
      { currency },
    ),
    moneyColumn<Line>(
      'credit',
      t('lines.credit'),
      (l) => (l.side === 'CREDIT' ? l.amount.amount : null),
      { currency },
    ),
    textColumn<Line>('rule', t('lines.rule'), (l) => l.ruleCode),
  ];
}

function JournalCard({ journal }: { journal: JournalView }) {
  const { t, i18n } = useTranslation('finance');
  const fmt = useFormat();
  const totals = journalTotals(journal.lines);
  const currency = journal.lines[0]?.amount.currency ?? 'EUR';
  const columns = useMemo(
    () => lineColumns((k) => t(k), i18n.language, currency),
    [t, i18n.language, currency],
  );
  return (
    <Section title={t('journal.title', { number: journal.journalNumber })} headingLevel={3}>
      <KeyValueList
        aria-label={t('journal.title', { number: journal.journalNumber })}
        items={[
          { id: 'event', label: t('journal.event'), value: journal.sourceEventType, kind: 'mono' },
          { id: 'book', label: t('journal.book'), value: journal.book },
          {
            id: 'date',
            label: t('journal.accountingDate'),
            value: fmt.date(journal.accountingDate),
          },
          { id: 'period', label: t('journal.period'), value: journal.period },
          ...(journal.reversesJournalId
            ? [
                {
                  id: 'reverses',
                  label: t('journal.reverses'),
                  value: journal.reversesJournalId,
                  kind: 'mono' as const,
                },
              ]
            : []),
          ...(journal.reversedByJournalId
            ? [
                {
                  id: 'reversedBy',
                  label: t('journal.reversedBy'),
                  value: journal.reversedByJournalId,
                  kind: 'mono' as const,
                },
              ]
            : []),
        ]}
      />
      <SimpleTable<Line>
        aria-label={t('journal.linesLabel', { number: journal.journalNumber })}
        columns={columns}
        data={journal.lines}
        getRowId={(l) => String(l.lineNo)}
      />
      <div className={styles.row}>
        <StatusPill
          semantic={totals.balanced ? 'success' : 'error'}
          subLabel={totals.balanced ? t('journal.balanced') : t('journal.unbalanced')}
          announceChanges={false}
        />
        <span className={styles.money}>
          {t('journal.totals', {
            debit: fmt.money({ amount: totals.debit, currency }),
            credit: fmt.money({ amount: totals.credit, currency }),
          })}
        </span>
      </div>
    </Section>
  );
}

/** Journal entries of a policy (fin.Journal.query): debit and credit lines with a balance check. Read-only. */
export function JournalPage() {
  const { policyNumber = '' } = useParams();
  const { t } = useTranslation('finance');
  const query = useJournals(policyNumber);
  const pages = query.data?.pages;
  const journals = pages?.flatMap((p) => p.items).map((i) => i.journal) ?? [];
  return (
    <div className={styles.page}>
      <PageHeader
        overline={t('overline')}
        title={t('title', { number: policyNumber })}
        subtitle={<span className="ds-caption">{t('readOnly')}</span>}
        actions={
          <LinkButton variant="secondary" to="/finance">
            {t('another')}
          </LinkButton>
        }
      />
      <QueryView query={{ ...query, data: pages }}>
        {() =>
          journals.length === 0 ? (
            <EmptyState
              kind="first-use"
              headingLevel={2}
              headline={t('emptyTitle')}
              description={t('emptyBody')}
            />
          ) : (
            <div className={styles.stack}>
              {journals.map((journal) => (
                <JournalCard key={journal.journalId} journal={journal} />
              ))}
              {query.hasNextPage ? (
                <div className={styles.actions}>
                  <Button
                    variant="secondary"
                    isLoading={query.isFetchingNextPage}
                    onPress={() => {
                      void query.fetchNextPage();
                    }}
                  >
                    {t('loadMore')}
                  </Button>
                </div>
              ) : null}
            </div>
          )
        }
      </QueryView>
    </div>
  );
}
