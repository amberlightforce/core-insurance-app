import { parseDate } from '@internationalized/date';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import {
  Banner,
  Button,
  DatePicker,
  EmptyState,
  StatusPill,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useArchiveCompleteness, type CompletenessReport } from './api';
import local from './documents.module.css';

type Period = CompletenessReport['periods'][number];

function isoDaysAgo(days: number): string {
  const d = new Date();
  d.setUTCDate(d.getUTCDate() - days);
  return d.toISOString().slice(0, 10);
}

function dateValue(iso: string) {
  try {
    return parseDate(iso);
  } catch {
    return null;
  }
}

/** Archive completeness (admin): issued documents against stored archive entries and their hashes, per month. */
export function CompletenessPanel() {
  const { t } = useTranslation('documents');
  const [from, setFrom] = useState(() => isoDaysAgo(90));
  const [to, setTo] = useState(() => isoDaysAgo(0));
  const [run, setRun] = useState<{ from: string; to: string } | null>(null);
  const query = useArchiveCompleteness(run?.from ?? '', run?.to ?? '', run !== null);

  const columns = useMemo<DataColumn<Period>[]>(
    () => [
      textColumn<Period>('period', t('completeness.period'), (p) => p.period),
      textColumn<Period>('issued', t('completeness.issued'), (p) => String(p.issued)),
      textColumn<Period>('ok', t('completeness.ok'), (p) => String(p.ok)),
      statusColumn<Period>(
        'gaps',
        t('completeness.gaps'),
        (p) => String(p.gaps.length),
        (p) =>
          p.gaps.length === 0 ? (
            <StatusPill semantic="success" text={t('completeness.none')} announceChanges={false} />
          ) : (
            <StatusPill
              semantic="error"
              text={t('completeness.gapCount', { count: p.gaps.length })}
              announceChanges={false}
            />
          ),
      ),
      textColumn<Period>('orphans', t('completeness.orphans'), (p) =>
        String(p.orphanArchiveIds.length),
      ),
    ],
    [t],
  );

  return (
    <Section title={t('completeness.title')}>
      <p className={styles.muted}>{t('completeness.lead')}</p>
      <div className={local.range}>
        <DatePicker
          label={t('filters.from')}
          value={dateValue(from)}
          onChange={(v) => {
            if (v) setFrom(v.toString());
          }}
        />
        <DatePicker
          label={t('filters.to')}
          value={dateValue(to)}
          onChange={(v) => {
            if (v) setTo(v.toString());
          }}
        />
        <Button
          variant="primary"
          isLoading={run !== null && query.isFetching}
          onPress={() => {
            setRun({ from, to });
          }}
        >
          {t('completeness.run')}
        </Button>
      </div>
      {run ? (
        <QueryView query={query}>
          {(report) => (
            <div className={local.periods}>
              {report.truncated ? (
                <Banner variant="warning" live="none" title={t('completeness.truncated')}>
                  {t('completeness.truncatedBody')}
                </Banner>
              ) : null}
              <SimpleTable<Period>
                aria-label={t('completeness.title')}
                columns={columns}
                data={report.periods}
                getRowId={(p) => p.period}
                getRowLabel={(p) => p.period}
                emptyState={
                  <EmptyState
                    kind="first-use"
                    illustration={<></>}
                    headingLevel={3}
                    headline={t('completeness.emptyTitle')}
                    description={t('completeness.emptyBody')}
                  />
                }
              />
              {report.periods
                .flatMap((p) => p.gaps)
                .map((g) => (
                  <Banner
                    key={`${g.documentId}-${g.kind}`}
                    variant="danger"
                    live="none"
                    title={`${g.documentNumber} · ${t(`completeness.kinds.${g.kind}`)}`}
                  >
                    <span className="ds-mono">{g.archiveId}</span>
                  </Banner>
                ))}
            </div>
          )}
        </QueryView>
      ) : null}
    </Section>
  );
}
