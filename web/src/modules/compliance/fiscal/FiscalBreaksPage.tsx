import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Button,
  Select,
  TextField,
  announce,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  breakKinds,
  documentNumber,
  useFiscalBreaks,
  useReconciliationRun,
  type BreakStatus,
  type FiscalBreak,
} from './api';
import { BreakKindPill, BreakStatusPill } from './BreakPills';
import { BulkResubmitDialog } from './BulkResubmitDialog';
import styles from './Fiscal.module.css';

const ALL = 'ALL';
const date = /^\d{4}-\d{2}-\d{2}$/;

/**
 * Fiscal reconciliation breaks (BO-CMP-14 b, BO-CMP-07): run the reconciliation, review open and resolved breaks.
 * There is no resolve action: a break closes on the first later run that no longer finds it.
 */
export function FiscalBreaksPage() {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const navigate = useNavigate();
  const { keyFor, release } = useIdempotencyKey();
  const run = useReconciliationRun();
  const [status, setStatus] = useState<BreakStatus>('OPEN');
  const [kind, setKind] = useState(ALL);
  const [upTo, setUpTo] = useState('');
  const [tried, setTried] = useState(false);
  const [bulk, setBulk] = useState(false);
  const breaks = useFiscalBreaks(status);
  const dateInvalid = upTo.trim() !== '' && !date.test(upTo.trim());

  const start = () => {
    setTried(true);
    if (dateInvalid) return;
    const body = { date: upTo.trim() };
    run.mutate(
      { date: body.date, key: keyFor(body) },
      {
        onSuccess: () => {
          release();
          announce(t('fiscal.breaksPage.run.done'));
        },
      },
    );
  };

  const columns = useMemo<DataColumn<FiscalBreak>[]>(
    () => [
      statusColumn<FiscalBreak>(
        'kind',
        t('fiscal.columns.kind'),
        (b) => b.kind ?? '',
        (b) => <BreakKindPill kind={b.kind ?? ''} />,
        { size: 230 },
      ),
      identifierColumn<FiscalBreak>('number', t('fiscal.columns.number'), documentNumber, {
        size: 150,
      }),
      textColumn<FiscalBreak>('detail', t('fiscal.columns.detail'), (b) => b.detail ?? '—', {
        size: 340,
      }),
      statusColumn<FiscalBreak>(
        'status',
        t('fiscal.columns.status'),
        (b) => b.status ?? '',
        (b) => <BreakStatusPill status={b.status ?? 'OPEN'} />,
        { size: 130 },
      ),
      textColumn<FiscalBreak>(
        'total',
        t('fiscal.columns.total'),
        (b) => fmt.money(b.document.total),
        { size: 130 },
      ),
      textColumn<FiscalBreak>(
        'detected',
        t('fiscal.columns.detected'),
        (b) => fmt.dateTime(b.detectedAt),
        { size: 170 },
      ),
      textColumn<FiscalBreak>(
        'lastSeen',
        t('fiscal.columns.lastSeen'),
        (b) => fmt.dateTime(b.lastSeenAt),
        { size: 170 },
      ),
    ],
    [t, fmt],
  );

  const result = run.data;
  const stats: [string, number][] = result
    ? [
        ['checked', result.checked],
        ['opened', result.opened],
        ['reopened', result.reopened],
        ['resolved', result.resolved],
        ['open', result.open],
      ]
    : [];

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('fiscal.overline')}
        title={t('fiscal.breaksPage.title')}
        description={t('fiscal.breaksPage.lead')}
        actions={
          <LinkButton variant="secondary" to="/compliance/fiscal">
            {t('fiscal.breaksPage.back')}
          </LinkButton>
        }
      />
      <Section title={t('fiscal.breaksPage.run.title')} family="info">
        <p className={staff.muted}>{t('fiscal.breaksPage.run.lead')}</p>
        <form
          className={styles.runRow}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            start();
          }}
        >
          <TextField
            label={t('fiscal.breaksPage.run.date')}
            helperText={t('fiscal.breaksPage.run.dateHelp')}
            mono
            value={upTo}
            onChange={setUpTo}
            errorMessage={tried && dateInvalid ? t('fiscal.breaksPage.run.dateInvalid') : undefined}
          />
          <Button type="submit" variant="primary" isLoading={run.isPending}>
            {t('fiscal.breaksPage.run.button')}
          </Button>
        </form>
        {run.isError ? (
          <ProblemBanner error={run.error} title={t('fiscal.breaksPage.run.failed')} />
        ) : null}
        {result ? (
          <dl className={styles.facts} aria-label={t('fiscal.breaksPage.run.done')}>
            {stats.map(([key, value]) => (
              <div key={key}>
                <dt>{t(`fiscal.breaksPage.run.${key}`)}</dt>
                <dd>{value}</dd>
              </div>
            ))}
          </dl>
        ) : null}
      </Section>
      <Section
        title={t('fiscal.breaksPage.list.title')}
        family="danger"
        {...(breaks.data ? { count: breaks.data.length } : {})}
        actions={
          status === 'OPEN' ? (
            <Button
              variant="secondary"
              isDisabled={!breaks.data?.some((b) => b.document.status === 'REJECTED')}
              onPress={() => {
                setBulk(true);
              }}
            >
              {t('fiscal.bulk.open')}
            </Button>
          ) : undefined
        }
      >
        <div className={styles.filters}>
          <Select
            label={t('fiscal.breaksPage.filter.status')}
            options={(['OPEN', 'RESOLVED'] as const).map((s) => ({
              id: s,
              label: t(`fiscal.breakStatus.${s}`),
            }))}
            value={status}
            onChange={(v) => {
              if (v === 'OPEN' || v === 'RESOLVED') setStatus(v);
            }}
          />
          <Select
            label={t('fiscal.breaksPage.filter.kind')}
            options={[
              { id: ALL, label: t('fiscal.breaksPage.filter.allKinds') },
              ...breakKinds.map((k) => ({ id: k, label: t(`fiscal.breakKind.${k}`) })),
            ]}
            value={kind}
            onChange={(v) => {
              if (v) setKind(v);
            }}
          />
        </div>
        <QueryView query={breaks}>
          {(rows) => (
            <SimpleTable<FiscalBreak>
              aria-label={t('fiscal.breaksPage.list.title')}
              columns={columns}
              data={kind === ALL ? rows : rows.filter((b) => b.kind === kind)}
              getRowId={(b) => b.breakId ?? `${b.document.fiscalDocumentId}-${b.kind ?? ''}`}
              getRowLabel={documentNumber}
              onOpen={(b) => {
                void navigate(`/compliance/fiscal/${b.document.fiscalDocumentId}`);
              }}
              emptyState={
                <p className="ds-caption">
                  {status === 'OPEN'
                    ? t('fiscal.breaksPage.list.empty')
                    : t('fiscal.breaksPage.list.emptyResolved')}
                </p>
              }
            />
          )}
        </QueryView>
      </Section>
      {bulk && breaks.data ? (
        <BulkResubmitDialog
          rows={breaks.data}
          onClose={() => {
            setBulk(false);
          }}
        />
      ) : null}
    </div>
  );
}
