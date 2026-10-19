import { parseDate, today } from '@internationalized/date';
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
  type HoldRequest,
} from './api';
import { canActOnDelinquency, daysRemaining } from './roles';

function Countdown({ c }: { c: DelinquencyCase }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const days = daysRemaining(c.deadline);
  if (days === null) {
    return (
      <Banner variant="info" live="none" title={t('delinquency.clock.none')}>
        {c.state === 'NOTICE_RETURNED'
          ? t('delinquency.clock.returned')
          : t('delinquency.clock.noProof')}
      </Banner>
    );
  }
  return (
    <Banner
      variant={days < 0 ? 'danger' : days <= 5 ? 'warning' : 'info'}
      live="none"
      title={days < 0 ? t('delinquency.clock.overdue') : t('delinquency.daysLeft', { count: days })}
    >
      {t('delinquency.clock.due', { date: fmt.dateTime(c.deadline) })}
    </Banner>
  );
}

function ActionForms({ c }: { c: DelinquencyCase }) {
  const { t } = useTranslation('billing');
  const contactKey = useIdempotencyKey();
  const arrangeKey = useIdempotencyKey();
  const holdKey = useIdempotencyKey();
  const [channel, setChannel] = useState('PHONE');
  const [outcome, setOutcome] = useState('');
  const [note, setNote] = useState('');
  const [promised, setPromised] = useState(today('Europe/Athens').add({ days: 7 }).toString());
  const [arrangeNote, setArrangeNote] = useState('');
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const allowed = canActOnDelinquency();
  const resolved = c.state === 'RESOLVED';

  const contact = useDelinquencyAction('record-contact', () => {
    contactKey.release();
    setOutcome('');
    setNote('');
    announce(t('delinquency.contact.done'));
  });
  const arrange = useDelinquencyAction('arrange', () => {
    arrangeKey.release();
    announce(t('delinquency.arrange.done'));
  });
  const onHold = c.state === 'ON_HOLD';
  const hold = useDelinquencyAction('release-hold', () => {
    holdKey.release();
    setReason('');
    announce(onHold ? t('delinquency.hold.done') : t('delinquency.hold.placed'));
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
              if (v) setChannel(v);
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
                data: {
                  delinquencyId: c.delinquencyId,
                  channel,
                  outcome: outcome.trim(),
                  ...(note.trim() ? { note: note.trim() } : {}),
                },
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
            value={parseDate(promised)}
            onChange={(v) => {
              if (v) setPromised(v.toString());
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
                data: {
                  delinquencyId: c.delinquencyId,
                  promiseDate: promised,
                  ...(arrangeNote.trim() ? { note: arrangeNote.trim() } : {}),
                },
              };
              arrange.mutate({ request, key: arrangeKey.keyFor(request) });
            }}
          >
            {t('delinquency.arrange.submit')}
          </Button>
        </form>
      </Section>
      {!resolved && c.state !== 'CANCELLATION_REQUESTED' ? (
        <Section
          title={onHold ? t('delinquency.hold.title') : t('delinquency.hold.placeTitle')}
          family="plum"
        >
          <div className={styles.stack}>
            {onHold ? (
              c.holdReason ? (
                <p className={styles.muted}>{c.holdReason}</p>
              ) : null
            ) : (
              <TextField
                label={t('delinquency.hold.reason')}
                multiline
                value={reason}
                onChange={setReason}
                errorMessage={tried && reason.trim() === '' ? t('delinquency.required') : undefined}
              />
            )}
            {hold.isError ? (
              <ProblemBanner error={hold.error} title={t('delinquency.failed')} />
            ) : null}
            <Button
              variant={onHold ? 'primary' : 'danger'}
              isDisabled={!allowed || hold.isPending}
              onPress={() => {
                setTried(true);
                if (!onHold && reason.trim() === '') return;
                const request: HoldRequest = {
                  data: onHold
                    ? { delinquencyId: c.delinquencyId, release: true }
                    : { delinquencyId: c.delinquencyId, reason: reason.trim() },
                };
                hold.mutate({ request, key: holdKey.keyFor(request) });
              }}
            >
              {onHold ? t('delinquency.hold.submit') : t('delinquency.hold.place')}
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
  const timeline = [
    { at: c.openedAt, kind: 'opened', actor: null, note: null },
    ...(c.notificationDate
      ? [{ at: c.notificationDate, kind: 'notified', actor: null, note: null }]
      : []),
    ...(c.contacts ?? []).map((k) => ({
      at: k.recordedAt,
      kind: `${k.channel} · ${k.outcome}`,
      actor: k.recordedBy ?? null,
      note: k.note ?? null,
    })),
    ...(c.resolvedAt ? [{ at: c.resolvedAt, kind: 'resolved', actor: null, note: null }] : []),
  ].sort((x, y) => x.at.localeCompare(y.at));
  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('delinquency.detail.title')}
        recordId={c.delinquencyId.slice(0, 8)}
        subtitle={t(`delinquency.status.${c.state}`)}
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
              value: fmt.money(c.overdueAmount),
              kind: 'money',
            },
            {
              id: 'started',
              label: t('delinquency.columns.started'),
              value: fmt.dateTime(c.openedAt),
            },
            {
              id: 'promise',
              label: t('delinquency.detail.promise'),
              value: c.promiseDate ? fmt.date(c.promiseDate) : null,
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
              <li key={`${e.at}-${String(i)}`}>
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
