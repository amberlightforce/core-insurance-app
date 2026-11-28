import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import {
  Checkbox,
  Select,
  StatusPill,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import staff from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  complaintStatuses,
  daysUntil,
  isOverdue,
  nextDeadline,
  useComplaintList,
  type ComplaintItem,
} from './api';
import styles from './Complaints.module.css';

const ALL = 'ALL';

type Semantic = 'disabled' | 'pending-approval' | 'info' | 'success' | 'warning' | 'error';

const statusSemantic: Record<string, Semantic> = {
  RECEIVED: 'info',
  ACKNOWLEDGED: 'info',
  UNDER_INVESTIGATION: 'pending-approval',
  ANSWERED: 'success',
  ESCALATED: 'warning',
  CLOSED: 'disabled',
};

const unique = (values: (string | undefined)[]): string[] =>
  [...new Set(values.filter((v): v is string => !!v))].sort();

/** «CLAIM_HANDLING» → «Claim handling» (class and channel codes are open vocabularies, so no fixed catalogue). */
const humanize = (v: string): string => {
  const text = v.replace(/_/g, ' ').toLowerCase();
  return text.charAt(0).toUpperCase() + text.slice(1);
};

/** Complaints list: filter by status, class, channel and overdue; deadline countdown; link to the case. */
export function ComplaintListPage() {
  const { t } = useTranslation('compliance');
  const fmt = useFormat();
  const navigate = useNavigate();
  const [status, setStatus] = useState(ALL);
  const [category, setCategory] = useState(ALL);
  const [channel, setChannel] = useState(ALL);
  const [overdueOnly, setOverdueOnly] = useState(false);
  // One «now» per page view (lazy initial state keeps render pure).
  const [now] = useState(() => Date.now());
  const query = useComplaintList(status === ALL ? '' : status);

  const columns = useMemo<DataColumn<ComplaintItem>[]>(() => {
    return [
      identifierColumn<ComplaintItem>(
        'number',
        t('complaints.columns.number'),
        (c) => c.number ?? c.complaint?.number ?? '—',
        { size: 170 },
      ),
      statusColumn<ComplaintItem>(
        'status',
        t('complaints.columns.status'),
        (c) => c.complaint?.status ?? '',
        (c) => {
          const s = c.complaint?.status ?? '';
          return (
            <StatusPill
              semantic={statusSemantic[s] ?? 'disabled'}
              text={t(`complaints.status.${s}`, { defaultValue: s || '—' })}
            />
          );
        },
        { size: 190 },
      ),
      textColumn<ComplaintItem>(
        'category',
        t('complaints.columns.category'),
        (c) => (c.complaint?.category ? humanize(c.complaint.category) : '—'),
        { size: 170 },
      ),
      textColumn<ComplaintItem>(
        'channel',
        t('complaints.columns.channel'),
        (c) => (c.complaint?.channel ? humanize(c.complaint.channel) : '—'),
        { size: 130 },
      ),
      textColumn<ComplaintItem>(
        'received',
        t('complaints.columns.received'),
        (c) => fmt.date(c.complaint?.receivedAt),
        { size: 130 },
      ),
      statusColumn<ComplaintItem>(
        'deadline',
        t('complaints.columns.deadline'),
        (c) => nextDeadline(c)?.deadline ?? '',
        (c) => {
          const next = nextDeadline(c);
          if (!next?.deadline) return <span aria-hidden="true">—</span>;
          const days = daysUntil(next.deadline, now);
          const late = days < 0 && c.complaint?.status !== 'CLOSED';
          return (
            <span className={styles.countdown}>
              <span>{fmt.date(next.deadline)}</span>
              <span className={late ? styles.late : 'ds-caption'}>
                {late
                  ? t('complaints.countdown.late', { count: -days })
                  : t('complaints.countdown.left', { count: days })}
              </span>
            </span>
          );
        },
        { size: 240 },
      ),
      statusColumn<ComplaintItem>(
        'overdue',
        t('complaints.columns.overdue'),
        (c) => (isOverdue(c, now) ? 'OVERDUE' : ''),
        (c) =>
          isOverdue(c, now) ? (
            <StatusPill semantic="error" text={t('complaints.overdue')} />
          ) : (
            <span aria-hidden="true">—</span>
          ),
        { size: 130 },
      ),
    ];
  }, [t, fmt, now]);

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('complaints.overline')}
        title={t('complaints.title')}
        description={t('complaints.lead')}
      />
      <Section
        title={t('complaints.list.title')}
        family="brand"
        {...(query.data ? { count: query.data.items.length } : {})}
      >
        <QueryView query={query}>
          {(page) => {
            const rows = page.items.filter(
              (c) =>
                (category === ALL || c.complaint?.category === category) &&
                (channel === ALL || c.complaint?.channel === channel) &&
                (!overdueOnly || isOverdue(c, now)),
            );
            const options = (values: string[]) => [
              { id: ALL, label: t('complaints.filter.all') },
              ...values.map((v) => ({ id: v, label: humanize(v) })),
            ];
            return (
              <>
                <div className={styles.filters}>
                  <Select
                    label={t('complaints.filter.status')}
                    options={[
                      { id: ALL, label: t('complaints.filter.all') },
                      ...complaintStatuses.map((s) => ({
                        id: s,
                        label: t(`complaints.status.${s}`),
                      })),
                    ]}
                    value={status}
                    onChange={(v) => {
                      if (v) setStatus(v);
                    }}
                  />
                  <Select
                    label={t('complaints.filter.category')}
                    options={options(unique(page.items.map((c) => c.complaint?.category)))}
                    value={category}
                    onChange={(v) => {
                      if (v) setCategory(v);
                    }}
                  />
                  <Select
                    label={t('complaints.filter.channel')}
                    options={options(unique(page.items.map((c) => c.complaint?.channel)))}
                    value={channel}
                    onChange={(v) => {
                      if (v) setChannel(v);
                    }}
                  />
                  <Checkbox isSelected={overdueOnly} onChange={setOverdueOnly}>
                    {t('complaints.filter.overdue')}
                  </Checkbox>
                </div>
                <SimpleTable<ComplaintItem>
                  aria-label={t('complaints.list.title')}
                  columns={columns}
                  data={rows}
                  getRowId={(c) => c.complaint?.complaintId ?? c.number ?? ''}
                  getRowLabel={(c) => c.number ?? ''}
                  onOpen={(c) => {
                    if (c.complaint?.complaintId) {
                      void navigate(`/compliance/complaints/${c.complaint.complaintId}`);
                    }
                  }}
                  emptyState={<p className="ds-caption">{t('complaints.list.empty')}</p>}
                />
              </>
            );
          }}
        </QueryView>
      </Section>
    </div>
  );
}
