import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Button,
  Dialog,
  EmptyState,
  StatusPill,
  TextField,
  announce,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useCloseAction, useCloseTasks, usePeriods, useSignOffTask, type CloseAction } from './api';
import type { ClosePeriodView, CloseTask, FinancePeriod } from './types';

/** The close step each period state offers: soft close, then lock (by a second person), and a reopen that needs a second approver. */
const stepsOf = (state: string): CloseAction[] =>
  state === 'OPEN' ? ['soft-close'] : state === 'SOFT_CLOSED' ? ['lock', 'reopen'] : ['reopen'];

export function PeriodStatePill({ state }: { state: string }) {
  const { t } = useTranslation('finance');
  const semantic = state === 'OPEN' ? 'success' : state === 'SOFT_CLOSED' ? 'warning' : 'locked';
  return (
    <StatusPill
      semantic={semantic}
      text={t(`ops.close.state.${state}`, { defaultValue: state })}
      announceChanges={false}
    />
  );
}

function TaskRow({
  period,
  task,
  editable,
}: {
  period: string;
  task: CloseTask;
  editable: boolean;
}) {
  const { t } = useTranslation('finance');
  const { keyFor, release } = useIdempotencyKey();
  const signOff = useSignOffTask();
  const done = task.status === 'SIGNED_OFF';
  const body = { period, task: task.task };
  return (
    <li className={styles.stack}>
      <div className={styles.actions}>
        <strong>{t(`ops.close.task.${task.task}`, { defaultValue: task.task })}</strong>
        <StatusPill
          semantic={done ? 'success' : 'warning'}
          text={t(done ? 'ops.close.taskDone' : 'ops.close.taskOpen')}
          announceChanges={false}
        />
        {done && task.signedOffBy ? (
          <span>{t('ops.close.signedBy', { who: task.signedOffBy })}</span>
        ) : null}
        {editable && !done ? (
          <Button
            variant="secondary"
            isDisabled={signOff.isPending || Boolean(task.blocker)}
            isLoading={signOff.isPending}
            onPress={() => {
              signOff.mutate({ ...body, key: keyFor(body) }, { onSuccess: release });
            }}
          >
            {t('ops.close.signOff')}
          </Button>
        ) : null}
      </div>
      {!done && task.blocker ? <span>{task.blocker}</span> : null}
      {signOff.isError ? (
        <ProblemBanner error={signOff.error} title={t('ops.close.failed')} />
      ) : null}
    </li>
  );
}

function Checklist({ view }: { view: ClosePeriodView }) {
  const { t } = useTranslation('finance');
  return (
    <ul className={styles.stack} aria-label={t('ops.close.tasks')}>
      {view.tasks.map((task) => (
        <TaskRow
          key={task.task}
          period={view.period}
          task={task}
          editable={view.state === 'OPEN'}
        />
      ))}
    </ul>
  );
}

function Actions({ period, onDone }: { period: FinancePeriod; onDone: () => void }) {
  const { t } = useTranslation('finance');
  const tasks = useCloseTasks(period.period);
  const view = tasks.data;
  const { keyFor, release } = useIdempotencyKey();
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const [pending, setPending] = useState<CloseAction | null>(null);
  const [confirm, setConfirm] = useState<CloseAction | null>(null);
  const mutation = useCloseAction(() => {
    release();
    announce(t('ops.close.done', { period: period.period }));
    setReason('');
    setTried(false);
    onDone();
  });
  const missing = reason.trim() === '';
  const start = (action: CloseAction) => {
    setTried(true);
    if (missing) return;
    // Locking and reopening change what other modules may post: ask first.
    if (action === 'soft-close') run(action);
    else setConfirm(action);
  };
  const run = (action: CloseAction) => {
    setPending(action);
    const body = { action, period: period.period, reason: reason.trim() };
    mutation.mutate({ ...body, key: keyFor(body) });
  };
  const reopenPending = Boolean(view?.reopenRequestedBy);
  return (
    <div className={styles.stack}>
      {tasks.isError ? <ProblemBanner error={tasks.error} title={t('ops.close.failed')} /> : null}
      {view ? <Checklist view={view} /> : null}
      {reopenPending ? (
        <p role="status">
          {t('ops.close.reopenPending', {
            who: view?.reopenRequestedBy,
            reason: view?.reopenReason,
          })}
        </p>
      ) : null}
      <TextField
        label={t('ops.close.reason')}
        helperText={t('ops.close.reasonHelp')}
        multiline
        maxLength={1000}
        value={reason}
        onChange={setReason}
        errorMessage={tried && missing ? t('ops.close.reasonRequired') : undefined}
      />
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('ops.close.failed')} />
      ) : null}
      <div className={styles.actions}>
        {stepsOf(period.state).map((action) => (
          <Button
            key={action}
            variant={action === 'reopen' ? 'secondary' : 'primary'}
            isDisabled={mutation.isPending}
            isLoading={mutation.isPending && pending === action}
            onPress={() => {
              start(action);
            }}
          >
            {action === 'reopen' && reopenPending
              ? t('ops.close.action.approveReopen')
              : t(`ops.close.action.${action}`)}
          </Button>
        ))}
      </div>
      <Dialog
        title={confirm ? t(`ops.close.confirm.${confirm}.title`, { period: period.period }) : ''}
        tone="warning"
        isOpen={confirm !== null}
        onOpenChange={(open) => {
          if (!open) setConfirm(null);
        }}
        primaryAction={{
          label: confirm ? t(`ops.close.action.${confirm}`) : '',
          onAction: () => {
            if (confirm) run(confirm);
          },
        }}
      >
        <p>{confirm ? t(`ops.close.confirm.${confirm}.body`) : null}</p>
      </Dialog>
    </div>
  );
}

/** Period close (fin.Period.list, fin.Close.softClose / lock / reopen): the state of every accounting month and its next close step. */
export function PeriodClosePage() {
  const { t } = useTranslation('finance');
  const query = usePeriods();
  const [selected, setSelected] = useState<string | null>(null);
  const columns: DataColumn<FinancePeriod>[] = [
    textColumn<FinancePeriod>('period', t('ops.close.period'), (p) => p.period, { size: 140 }),
    statusColumn<FinancePeriod>(
      'state',
      t('ops.close.stateLabel'),
      (p) => p.state,
      (p) => <PeriodStatePill state={p.state} />,
    ),
    textColumn<FinancePeriod>('posting', t('ops.close.posting'), (p) =>
      p.closedForPosting ? t('ops.close.closedForPosting') : t('ops.close.openForPosting'),
    ),
  ];
  const current = query.data?.items.find((p) => p.period === selected);
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('ops.close.title')}
        overline={t('overline')}
        description={t('ops.close.lead')}
        actions={
          <LinkButton variant="secondary" to="/finance">
            {t('ops.back')}
          </LinkButton>
        }
      />
      <Section title={t('ops.close.periods')}>
        <QueryView query={query}>
          {(page) =>
            page.items.length === 0 ? (
              <EmptyState
                kind="first-use"
                illustration={<></>}
                headingLevel={3}
                headline={t('ops.close.emptyTitle')}
                description={t('ops.close.emptyBody')}
              />
            ) : (
              <SimpleTable<FinancePeriod>
                aria-label={t('ops.close.periods')}
                columns={columns}
                data={page.items}
                getRowId={(p) => p.period}
                getRowLabel={(p) => p.period}
                onOpen={(p) => {
                  setSelected(p.period);
                }}
              />
            )
          }
        </QueryView>
      </Section>
      {current ? (
        <Section title={t('ops.close.actionsFor', { period: current.period })} family="plum">
          <Actions period={current} onDone={() => undefined} />
        </Section>
      ) : null}
    </div>
  );
}
