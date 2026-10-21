import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import {
  Banner,
  Button,
  Dialog,
  KeyValueList,
  Select,
  StatusPill,
  TextField,
  announce,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { problemOf } from '../staff/problem';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import {
  useClaimTask,
  useCompleteTask,
  useSkipTask,
  useTask,
  type ObjectRef,
  type Task,
} from './api';
import { objectLink, useQueueName, useTaskTitle } from './parts';
import { TaskFlags } from './TaskFlags';

const noteMax = 500;
/** TODO(hardening): outcome codes per pattern (WRK pattern configuration, PRD-13). */
const outcomes = ['RESOLVED', 'NO_ACTION_NEEDED', 'REFERRED_ON'] as const;

function CloseDialog({
  task,
  mode,
  onClose,
}: {
  task: Task;
  mode: 'complete' | 'skip';
  onClose: () => void;
}) {
  const { t } = useTranslation('work');
  const complete = useCompleteTask();
  const skip = useSkipTask();
  const mutation = mode === 'complete' ? complete : skip;
  const { keyFor, release } = useIdempotencyKey();
  const [outcome, setOutcome] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [tried, setTried] = useState(false);
  const missing = mode === 'complete' ? outcome === null : note.trim() === '';

  const problem = mutation.isError ? problemOf(mutation.error) : null;
  const error = problem
    ? problem.status === 403
      ? t('close.forbidden')
      : problem.status === 409
        ? t('close.conflict')
        : (problem.title ?? t('close.failed'))
    : undefined;

  const submit = () => {
    setTried(true);
    if (missing) return;
    const body = {
      id: task.id,
      ...(mode === 'complete' && outcome ? { outcome } : {}),
      ...(mode === 'complete' && note.trim() ? { note: note.trim() } : {}),
      ...(mode === 'skip' ? { reason: note.trim() } : {}),
    };
    return mutation
      .mutateAsync({ body, key: keyFor(body) })
      .then(() => {
        release();
        announce(t(`close.${mode}.done`));
        onClose();
      })
      .catch(() => {
        // Shown in the dialog banner; the input is kept.
      });
  };

  return (
    <Dialog
      title={t(`close.${mode}.title`)}
      size="md"
      isOpen
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
      onCancel={onClose}
      closeOnAction={false}
      cancelLabel={t('close.cancel')}
      {...(error ? { error } : {})}
      primaryAction={{ label: t(`close.${mode}.submit`), variant: 'primary', onAction: submit }}
    >
      <div className={styles.stack}>
        {mode === 'complete' ? (
          <Select
            label={t('close.outcome')}
            isRequired
            options={outcomes.map((id) => ({ id, label: t(`outcomes.${id}`) }))}
            value={outcome}
            onChange={(value) => {
              setOutcome(value);
            }}
            errorMessage={tried && outcome === null ? t('close.outcomeRequired') : undefined}
          />
        ) : null}
        <TextField
          label={mode === 'complete' ? t('close.note') : t('close.reason')}
          multiline
          minRows={3}
          maxLength={noteMax}
          showCount
          isRequired={mode === 'skip'}
          value={note}
          onChange={setNote}
          errorMessage={
            tried && mode === 'skip' && note.trim() === '' ? t('close.reasonRequired') : undefined
          }
        />
      </div>
    </Dialog>
  );
}

function ObjectValue({ object }: { object: ObjectRef }) {
  const to = objectLink(object);
  const label = `${object.module}/${object.type}/${object.id}`;
  return to ? <LinkButton to={to}>{label}</LinkButton> : <span className="ds-mono">{label}</span>;
}

function TaskDetail({ task }: { task: Task }) {
  const { t } = useTranslation('work');
  const fmt = useFormat();
  const title = useTaskTitle();
  const queueName = useQueueName();
  const claim = useClaimTask();
  const { keyFor, release } = useIdempotencyKey();
  const [closing, setClosing] = useState<'complete' | 'skip' | null>(null);
  const [claimError, setClaimError] = useState<string | null>(null);
  const open = task.state === 'OPEN';

  const takeIt = () => {
    setClaimError(null);
    const body = { id: task.id, rulesOrMe: 'me' as const };
    return claim
      .mutateAsync({ body, key: keyFor(body) })
      .then(() => {
        release();
        announce(t('claim.done'));
      })
      .catch((error: unknown) => {
        const problem = problemOf(error);
        setClaimError(
          problem.status === 409 ? t('claim.taken') : (problem.title ?? t('claim.failed')),
        );
      });
  };

  const assignee = task.assignedToMe
    ? t('task.assignedToMe')
    : task.assignee.type === 'QUEUE'
      ? t('task.unassigned')
      : task.assignee.id;

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={title(task)}
        recordId={task.number}
        subtitle={
          <>
            <StatusPill
              semantic={open ? 'pending-approval' : 'success'}
              text={t(`state.${task.state}`)}
              announceChanges={false}
            />
            <TaskFlags task={task} />
          </>
        }
        actions={
          <>
            {open && task.claimableByMe ? (
              <Button
                variant="primary"
                isLoading={claim.isPending}
                onPress={() => {
                  void takeIt();
                }}
              >
                {t('task.claim')}
              </Button>
            ) : null}
            {open ? (
              <>
                <Button
                  variant="primary"
                  {...(!task.assignedToMe ? { disabledReason: t('task.notYours') } : {})}
                  onPress={() => {
                    setClosing('complete');
                  }}
                >
                  {t('close.complete.open')}
                </Button>
                <Button
                  variant="secondary"
                  {...(!task.assignedToMe ? { disabledReason: t('task.notYours') } : {})}
                  onPress={() => {
                    setClosing('skip');
                  }}
                >
                  {t('close.skip.open')}
                </Button>
              </>
            ) : null}
            <LinkButton variant="secondary" to="/work">
              {t('task.back')}
            </LinkButton>
          </>
        }
      />
      {claimError ? <Banner variant="warning" live="status" title={claimError} /> : null}
      <div className={styles.grid}>
        <Section title={t('detail.task')}>
          <KeyValueList
            aria-label={t('detail.task')}
            items={[
              { id: 'number', label: t('detail.number'), value: task.number, kind: 'mono' },
              { id: 'queue', label: t('detail.queue'), value: queueName(task.queueCode) },
              { id: 'assignee', label: t('detail.assignee'), value: assignee },
              {
                id: 'priority',
                label: t('detail.priority'),
                value: t(`priority.${task.priority}`, { defaultValue: task.priority }),
              },
              { id: 'created', label: t('detail.created'), value: fmt.dateTime(task.createdAt) },
              { id: 'due', label: t('detail.due'), value: fmt.dateTime(task.targetAt) },
              {
                id: 'escalation',
                label: t('detail.escalation'),
                value: fmt.dateTime(task.escalationAt),
              },
            ]}
          />
        </Section>
        <Section title={t('detail.objects')}>
          <KeyValueList
            aria-label={t('detail.objects')}
            items={[
              {
                id: 'primary',
                label: t('detail.primaryObject'),
                value: <ObjectValue object={task.primaryObject} />,
              },
              ...task.secondaryObjects.map((object, index) => ({
                id: `secondary-${String(index)}`,
                label: t('detail.secondaryObject'),
                value: <ObjectValue object={object} />,
              })),
            ]}
          />
        </Section>
        {!open ? (
          <Section title={t('detail.closure')}>
            <KeyValueList
              aria-label={t('detail.closure')}
              items={[
                { id: 'closedAt', label: t('detail.closedAt'), value: fmt.dateTime(task.closedAt) },
                {
                  id: 'outcome',
                  label: t('detail.outcome'),
                  value: task.outcome
                    ? t(`outcomes.${task.outcome}`, { defaultValue: task.outcome })
                    : '—',
                },
                { id: 'note', label: t('detail.note'), value: task.note ?? '—' },
              ]}
            />
          </Section>
        ) : null}
      </div>
      {task.patternCode === 'WRK-MKT-PACK-ROLLBACK-REVIEW' ? (
        <Banner variant="info" live="none" title={t('detail.packRollbackHint')} />
      ) : null}
      {closing ? (
        <CloseDialog
          task={task}
          mode={closing}
          onClose={() => {
            setClosing(null);
          }}
        />
      ) : null}
    </div>
  );
}

/** One WRK task: facts, related objects, claim / complete / skip. The API decides who may do what. */
export function TaskDetailPage() {
  const { t } = useTranslation('work');
  const { activityId = '' } = useParams();
  const query = useTask(activityId);
  return (
    <QueryView query={query} notFoundMessage={t('detail.notFound')}>
      {(task) => <TaskDetail task={task} />}
    </QueryView>
  );
}
