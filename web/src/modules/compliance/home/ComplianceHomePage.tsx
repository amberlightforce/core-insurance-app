import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import {
  StatusPill,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  earliestDeadline,
  isOpenClock,
  scalar,
  useClocks,
  useComplaints,
  type ClockItem,
  type ComplaintItem,
} from './api';

type Semantic = 'disabled' | 'pending-approval' | 'info' | 'success' | 'warning' | 'error';

const clockSemantic: Record<string, Semantic> = {
  RUNNING: 'info',
  WARNED: 'warning',
  PAUSED: 'pending-approval',
  MET: 'success',
  BREACHED: 'error',
  ELAPSED: 'disabled',
  CANCELLED: 'disabled',
};

const subjectText = (c: ClockItem): string => scalar(c.subjectRef, ['id', 'ref']) || '—';
const complaintNumber = (c: ComplaintItem): string =>
  scalar(c.number, ['value', 'number']) || scalar(c.complaint, ['number']);

/** Compliance landing: statutory clocks, complaints, and the way into the DSAR console. */
export function ComplianceHomePage() {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const clocks = useClocks();
  const complaints = useComplaints();

  const clockColumns = useMemo<DataColumn<ClockItem>[]>(
    () => [
      identifierColumn<ClockItem>('code', t('home.clocks.columns.code'), (c) => c.clockCode, {
        size: 200,
      }),
      textColumn<ClockItem>('subject', t('home.clocks.columns.subject'), subjectText, {
        size: 260,
      }),
      statusColumn<ClockItem>(
        'status',
        t('home.clocks.columns.status'),
        (c) => c.status,
        (c) => (
          <StatusPill
            semantic={clockSemantic[c.status] ?? 'disabled'}
            text={t(`home.clocks.status.${c.status}`, { defaultValue: c.status })}
          />
        ),
        { size: 150 },
      ),
      textColumn<ClockItem>(
        'deadline',
        t('home.clocks.columns.deadline'),
        (c) => fmt.dateTime(c.deadline),
        { size: 170 },
      ),
    ],
    [t, fmt],
  );

  const complaintColumns = useMemo<DataColumn<ComplaintItem>[]>(
    () => [
      identifierColumn<ComplaintItem>(
        'number',
        t('home.complaints.columns.number'),
        (c) => complaintNumber(c) || '—',
        { size: 200 },
      ),
      textColumn<ComplaintItem>(
        'status',
        t('home.complaints.columns.status'),
        (c) => scalar(c.complaint, ['status', 'state']) || '—',
        { size: 180 },
      ),
      textColumn<ComplaintItem>(
        'deadline',
        t('home.complaints.columns.deadline'),
        (c) => fmt.dateTime(earliestDeadline(c.deadlines)),
        { size: 170 },
      ),
    ],
    [t, fmt],
  );

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('home.overline')}
        title={t('home.title')}
        description={t('home.lead')}
      />
      <Section
        title={t('home.clocks.title')}
        {...(clocks.data
          ? { count: clocks.data.items.filter(isOpenClock).length, meta: t('home.clocks.open') }
          : {})}
      >
        <QueryView query={clocks}>
          {(page) => (
            <SimpleTable<ClockItem>
              aria-label={t('home.clocks.title')}
              columns={clockColumns}
              data={[...page.items].sort((a, b) =>
                (a.deadline ?? '9999').localeCompare(b.deadline ?? '9999'),
              )}
              getRowId={(c) => c.instanceId}
              getRowLabel={(c) => c.clockCode}
              emptyState={<p className="ds-caption">{t('home.clocks.empty')}</p>}
            />
          )}
        </QueryView>
      </Section>
      <Section
        title={t('home.complaints.title')}
        {...(complaints.data ? { count: complaints.data.items.length } : {})}
      >
        <QueryView query={complaints}>
          {(page) => (
            <SimpleTable<ComplaintItem>
              aria-label={t('home.complaints.title')}
              columns={complaintColumns}
              data={page.items}
              getRowId={(c) => complaintNumber(c) || JSON.stringify(c)}
              getRowLabel={(c) => complaintNumber(c)}
              emptyState={<p className="ds-caption">{t('home.complaints.empty')}</p>}
            />
          )}
        </QueryView>
      </Section>
      <Section title={t('home.dsar.title')}>
        <p className={staff.muted}>{t('home.dsar.body')}</p>
        <LinkButton to="/compliance/dsar">{t('home.dsar.open')}</LinkButton>
      </Section>
    </div>
  );
}
