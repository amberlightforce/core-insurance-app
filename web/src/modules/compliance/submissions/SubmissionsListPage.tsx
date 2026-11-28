import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Button,
  Select,
  TextField,
  identifierColumn,
  statusColumn,
  textColumn,
  toast,
  type DataColumn,
} from '../../../design-system';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  submissionStatuses,
  useArchiveCompleteness,
  useSubmissionCommand,
  useSubmissions,
  type SubmissionView,
} from './api';
import { OverduePill, SubmissionStatusPill } from './StatusPills';
import styles from './Submissions.module.css';

const ALL = 'ALL';
const date = /^\d{4}-\d{2}-\d{2}$/;

/** Regulatory submissions workbench: filter, overdue markers, register a new submission, archive completeness. */
export function SubmissionsListPage() {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const navigate = useNavigate();
  const { keyFor, release } = useIdempotencyKey();
  const run = useSubmissionCommand();
  const [status, setStatus] = useState(ALL);
  const [type, setType] = useState('');
  const [regulator, setRegulator] = useState('');
  const query = useSubmissions({
    status: status === ALL ? '' : status,
    submissionType: type.trim(),
    regulator: regulator.trim(),
  });

  const [nType, setNType] = useState('');
  const [nRegulator, setNRegulator] = useState('');
  const [nPeriod, setNPeriod] = useState('');
  const [nDue, setNDue] = useState('');
  const [nLegal, setNLegal] = useState('Unverified');
  const [tried, setTried] = useState(false);

  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [checked, setChecked] = useState<{ from: string; to: string } | null>(null);
  const archive = useArchiveCompleteness(checked?.from ?? '', checked?.to ?? '', checked !== null);

  const createInvalid =
    nType.trim() === '' ||
    nRegulator.trim() === '' ||
    nPeriod.trim() === '' ||
    (nDue.trim() !== '' && !date.test(nDue.trim()));

  const create = () => {
    setTried(true);
    if (createInvalid) return;
    const command = {
      name: 'create' as const,
      body: {
        submissionType: nType.trim(),
        regulator: nRegulator.trim(),
        periodLabel: nPeriod.trim(),
        ...(nDue.trim() ? { dueDate: nDue.trim(), dueLegalStatus: nLegal } : {}),
      },
    };
    run.mutate(
      { command, key: keyFor(command) },
      {
        onSuccess: ({ submission }) => {
          release();
          toast.success({ title: t('submissions.created'), description: submission.number });
          void navigate(`/compliance/submissions/${submission.submissionId}`);
        },
      },
    );
  };

  const columns = useMemo<DataColumn<SubmissionView>[]>(
    () => [
      identifierColumn<SubmissionView>('number', t('submissions.columns.number'), (s) => s.number, {
        size: 170,
      }),
      textColumn<SubmissionView>('type', t('submissions.columns.type'), (s) => s.submissionType, {
        size: 180,
      }),
      textColumn<SubmissionView>(
        'regulator',
        t('submissions.columns.regulator'),
        (s) => s.regulator,
        {
          size: 120,
        },
      ),
      textColumn<SubmissionView>('period', t('submissions.columns.period'), (s) => s.periodLabel, {
        size: 120,
      }),
      statusColumn<SubmissionView>(
        'status',
        t('submissions.columns.status'),
        (s) => s.status,
        (s) => <SubmissionStatusPill status={s.status} />,
        { size: 170 },
      ),
      textColumn<SubmissionView>(
        'due',
        t('submissions.columns.due'),
        (s) => (s.dueDate ? fmt.date(s.dueDate) : '—'),
        { size: 130 },
      ),
      statusColumn<SubmissionView>(
        'overdue',
        t('submissions.columns.overdue'),
        (s) => (s.overdue ? 'OVERDUE' : ''),
        (s) => (s.overdue ? <OverduePill /> : <span aria-hidden="true">—</span>),
        { size: 130 },
      ),
    ],
    [t, fmt],
  );

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('submissions.overline')}
        title={t('submissions.title')}
        description={t('submissions.lead')}
      />
      <Section
        title={t('submissions.list.title')}
        family="brand"
        {...(query.data ? { count: query.data.items.length } : {})}
      >
        <div className={styles.filters}>
          <Select
            label={t('submissions.filter.status')}
            options={[
              { id: ALL, label: t('submissions.filter.all') },
              ...submissionStatuses.map((s) => ({ id: s, label: t(`submissions.status.${s}`) })),
            ]}
            value={status}
            onChange={(v) => {
              if (v) setStatus(v);
            }}
          />
          <TextField label={t('submissions.filter.type')} value={type} onChange={setType} />
          <TextField
            label={t('submissions.filter.regulator')}
            value={regulator}
            onChange={setRegulator}
          />
        </div>
        <QueryView query={query}>
          {(page) => (
            <SimpleTable<SubmissionView>
              aria-label={t('submissions.list.title')}
              columns={columns}
              data={page.items}
              getRowId={(s) => s.submissionId}
              getRowLabel={(s) => s.number}
              onOpen={(s) => {
                void navigate(`/compliance/submissions/${s.submissionId}`);
              }}
              emptyState={<p className="ds-caption">{t('submissions.list.empty')}</p>}
            />
          )}
        </QueryView>
      </Section>
      <Section title={t('submissions.new.title')} family="info">
        <form
          className={styles.form}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            create();
          }}
        >
          <p className={staff.muted}>{t('submissions.new.lead')}</p>
          <TextField
            label={t('submissions.new.type')}
            helperText={t('submissions.new.typeHelp')}
            isRequired
            value={nType}
            onChange={setNType}
            errorMessage={tried && nType.trim() === '' ? t('submissions.required') : undefined}
          />
          <TextField
            label={t('submissions.new.regulator')}
            helperText={t('submissions.new.regulatorHelp')}
            isRequired
            value={nRegulator}
            onChange={setNRegulator}
            errorMessage={tried && nRegulator.trim() === '' ? t('submissions.required') : undefined}
          />
          <TextField
            label={t('submissions.new.period')}
            helperText={t('submissions.new.periodHelp')}
            isRequired
            value={nPeriod}
            onChange={setNPeriod}
            errorMessage={tried && nPeriod.trim() === '' ? t('submissions.required') : undefined}
          />
          <div className={styles.row}>
            <TextField
              label={t('submissions.new.due')}
              helperText={t('submissions.dateHelp')}
              mono
              value={nDue}
              onChange={setNDue}
              errorMessage={
                tried && nDue.trim() !== '' && !date.test(nDue.trim())
                  ? t('submissions.dateInvalid')
                  : undefined
              }
            />
            <Select
              label={t('submissions.new.legal')}
              options={['Unverified', 'Verified'].map((o) => ({
                id: o,
                label: t(`submissions.legal.${o}`),
              }))}
              value={nLegal}
              onChange={(v) => {
                if (v) setNLegal(v);
              }}
            />
          </div>
          <div className={styles.actions}>
            <Button type="submit" variant="primary" isLoading={run.isPending}>
              {t('submissions.new.submit')}
            </Button>
          </div>
          {run.isError ? (
            <ProblemBanner error={run.error} title={t('submissions.new.failed')} />
          ) : null}
        </form>
      </Section>
      <Section title={t('submissions.archive.title')} family="success">
        <p className={staff.muted}>{t('submissions.archive.lead')}</p>
        <div className={styles.row}>
          <TextField
            label={t('submissions.archive.from')}
            helperText={t('submissions.dateHelp')}
            mono
            value={from}
            onChange={setFrom}
          />
          <TextField
            label={t('submissions.archive.to')}
            helperText={t('submissions.dateHelp')}
            mono
            value={to}
            onChange={setTo}
          />
          <Button
            variant="secondary"
            isDisabled={from.trim() === '' || to.trim() === ''}
            onPress={() => {
              setChecked({ from: from.trim(), to: to.trim() });
            }}
          >
            {t('submissions.archive.check')}
          </Button>
        </div>
        {checked ? (
          <QueryView query={archive}>
            {(report) => (
              <div className={styles.stack}>
                {report.truncated ? (
                  <p className="ds-caption" role="status">
                    {t('submissions.archive.truncated')}
                  </p>
                ) : null}
                <ul className={styles.list} aria-label={t('submissions.archive.title')}>
                  {report.periods.map((p) => (
                    <li key={p.period}>
                      <span className="ds-mono">{p.period}</span>{' '}
                      {t('submissions.archive.row', {
                        ok: p.ok,
                        issued: p.issued,
                        gaps: p.gaps.length,
                        orphans: p.orphanCount,
                      })}
                      {p.gaps.length > 0 ? (
                        <ul className={styles.list}>
                          {p.gaps.map((g) => (
                            <li key={`${g.documentId}-${g.kind}`} className="ds-caption">
                              <span className="ds-mono">{g.documentNumber}</span> · {g.kind}
                            </li>
                          ))}
                        </ul>
                      ) : null}
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </QueryView>
        ) : null}
      </Section>
    </div>
  );
}
