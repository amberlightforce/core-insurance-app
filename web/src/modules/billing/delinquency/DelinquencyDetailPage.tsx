import { today } from '@internationalized/date';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Banner,
  Button,
  DatePicker,
  KeyValueList,
  Select,
  TextField,
  announce,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  useDelinquency,
  useDelinquencyAction,
  type ArrangeRequest,
  type DelinquencyCase,
  type RecordContactRequest,
  type ReleaseHoldRequest,
} from './api';
import { canActOnDelinquency, daysRemaining } from './roles';

function Countdown({ c }: { c: DelinquencyCase }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const days = daysRemaining(c.clockDueAt);
  if (days === null) {
    return (
      <Banner variant="info" live="none" title={t('delinquency.clock.none')}>
        {c.status === 'NOTICE_RETURNED'
          ? t('delinquency.clock.returned')
          : t('delinquency.clock.noProof')}
      </Banner>
    );
  }
  return (
    <Banner
      variant={days < 0 ? 'error' : days <= 5 ? 'warning' : 'info'}
      live="none"
      title={days < 0 ? t('delinquency.clock.overdue') : t('delinquency.daysLeft', { count: days })}
    >
      {t('delinquency.clock.due', { date: fmt.dateTime(c.clockDueAt) })}
    </Banner>
  );
}

function ActionForms({ c }: { c: DelinquencyCase }) {
  const { t } = useTranslation('billing');
  const contactKey = useIdempotencyKey();
  const arrangeKey = useIdempotencyKey();
  const holdKey = useIdempotencyKey();
  const [channel, setChannel] = useState<RecordContactRequest['channel']>('PHONE');
  const [outcome, setOutcome] = useState('');
  const [note, setNote] = useState('');
  const [promised, setPromised] = useState(today('Europe/Athens').add({ days: 7 }).toString());
  const [arrangeNote, setArrangeNote] = useState('');
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const allowed = canActOnDelinquency();
  const resolved = c.status === 'RESOLVED';

  const contact = useDelinquencyAction<RecordContactRequest>('record-contact', () => {
    contactKey.release();
    setOutcome('');
    setNote('');
    announce(t('delinquency.contact.done'));
  });
  const arrange = useDelinquencyAction<ArrangeRequest>('arrange', () => {
    arrangeKey.release();
    announce(t('delinquency.arrange.done'));
  });
  const release = useDelinquencyAction<ReleaseHoldRequest>('release-hold', () => {
    holdKey.release();
    setReason('');
    announce(t('delinquency.hold.done'));
  });

  return (
    <div className={styles.grid}>
      {!allowed ? <Banner variant="info" live="none" title={t('delinquency.roleNote')} /> : null}
      <Section title={t('delinquency.contact.title')}>
        <form
          noValidate
          className={styles.stack}
          aria-label={t('delinquency.contact.title')}
          onSubmit={(e) => {
            e.preventDefault();
          }}
        >
          <Select
            label={t('delinquency.contact.channel')}
            id="delinquency-channel"
            options={(['PHONE', 'EMAIL', 'IN_PERSON'] as const).map((k) => ({
              id: k,
              label: t(`delinquency.contact.channels.${k}`),
            }))}
            value={channel}
            onChange={(v) => {
              if (v) setChannel(v as RecordContactRequest['channel']);
            }}
          />
          <TextField
            label={t('delinquency.contact.outcome')}
            value={outcome}
            onChange={setOutcome}
            errorMessage={tried && outcome.trim() === '' ? t('delinquency.required') : undefined}
          />
          <TextField label={t('delinquency.note')} multiline value={note} onChange={setNote} />
          {contact.isError ? (
            <ProblemBanner error={contact.error} title={t('delinquency.failed')} />
          ) : null}
          <Button
            variant="primary"
            isDisabled={!allowed || resolved || contact.isPending}
            onPress={() => {
              setTried(true);
              if (outcome.trim() === '') return;
              const request: RecordContactRequest = {
                delinquencyId: c.delinquencyId,
                channel,
                outcome: outcome.trim(),
                ...(note.trim() ? { note: note.trim() } : {}),
              };
              contact.mutate({ request, key: contactKey.keyFor(request) });
            }}
          >
            {t('delinquency.contact.submit')}
          </Button>
        </form>
      </Section>
      <Section title={t('delinquency.arrange.title')}>
        <form
          noValidate
          className={styles.stack}
          aria-label={t('delinquency.arrange.title')}
          onSubmit={(e) => {
            e.preventDefault();
          }}
        >
          <DatePicker
            label={t('delinquency.arrange.promisedDate')}
            value={promised}
            onChange={(v) => {
              if (v) setPromised(v);
            }}
          />
          <TextField
            label={t('delinquency.note')}
            multiline
            value={arrangeNote}
            onChange={setArrangeNote}
          />
          {arrange.isError ? (
            <ProblemBanner error={arrange.error} title={t('delinquency.failed')} />
          ) : null}
          <Button
            variant="secondary"
            isDisabled={!allowed || resolved || arrange.isPending}
            onPress={() => {
              const request: ArrangeRequest = {
                delinquencyId: c.delinquencyId,
                promisedDate: promised,
                ...(arrangeNote.trim() ? { note: arrangeNote.trim() } : {}),
              };
              arrange.mutate({ request, key: arrangeKey.keyFor(request) });
            }}
          >
            {t('delinquency.arrange.submit')}
          </Button>
        </form>
      </Section>
      {c.onHold || c.status === 'ON_HOLD' ? (
        <Section title={t('delinquency.hold.title')} family="plum">
          <div className={styles.stack}>
            <TextField
              label={t('delinquency.hold.reason')}
              multiline
              value={reason}
              onChange={setReason}
              errorMessage={tried && reason.trim() === '' ? t('delinquency.required') : undefined}
            />
            {release.isError ? (
              <ProblemBanner error={release.error} title={t('delinquency.failed')} />
            ) : null}
            <Button
              variant="danger"
              isDisabled={!allowed || release.isPending}
              onPress={() => {
                setTried(true);
                if (reason.trim() === '') return;
                const request: ReleaseHoldRequest = {
                  delinquencyId: c.delinquencyId,
                  reason: reason.trim(),
                };
                release.mutate({ request, key: holdKey.keyFor(request) });
              }}
            >
              {t('delinquency.hold.submit')}
            </Button>
          </div>
        </Section>
      ) : null}
    </div>
  );
}

function Details({ c }: { c: DelinquencyCase }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const timeline = [...(c.timeline ?? [])].sort((a, b) => a.at.localeCompare(b.at));
  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('delinquency.detail.title')}
        recordId={c.delinquencyId.slice(0, 8)}
        subtitle={t(`delinquency.status.${c.status}`)}
        actions={
          <LinkButton variant="secondary" to="/billing/delinquency">
            {t('delinquency.backToList')}
          </LinkButton>
        }
      />
      <Countdown c={c} />
      <Section title={t('delinquency.detail.summary')}>
        <KeyValueList
          aria-label={t('delinquency.detail.summary')}
          items={[
            {
              id: 'open',
              label: t('delinquency.columns.open'),
              value: fmt.money(c.openAmount),
              kind: 'money',
            },
            {
              id: 'started',
              label: t('delinquency.columns.started'),
              value: fmt.dateTime(c.startedAt),
            },
            {
              id: 'notified',
              label: t('delinquency.detail.notified'),
              value: c.notificationDate ? fmt.date(c.notificationDate) : null,
            },
          ]}
        />
      </Section>
      <Section title={t('delinquency.timeline.title')}>
        {timeline.length === 0 ? (
          <p className={styles.muted}>{t('delinquency.timeline.empty')}</p>
        ) : (
          <ol aria-label={t('delinquency.timeline.title')}>
            {timeline.map((e, i) => (
              <li key={`${e.at}-${i}`}>
                <strong>
                  {t(`delinquency.timeline.kinds.${e.kind}`, { defaultValue: e.kind })}
                </strong>{' '}
                <span className={styles.muted}>{fmt.dateTime(e.at)}</span>
                {e.actor ? <span> · {e.actor}</span> : null}
                {e.note ? <div>{e.note}</div> : null}
              </li>
            ))}
          </ol>
        )}
      </Section>
      <ActionForms c={c} />
    </div>
  );
}

export function DelinquencyDetailPage() {
  const { t } = useTranslation('billing');
  const { delinquencyId = '' } = useParams();
  const query = useDelinquency(delinquencyId);
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('delinquency.notFound')}>
        {(c) => <Details c={c} />}
      </QueryView>
    </div>
  );
}
