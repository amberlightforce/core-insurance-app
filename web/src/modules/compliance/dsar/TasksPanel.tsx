import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Button, Checkbox, cx, toast } from '../../../design-system';
import {
  useDsarCommand,
  type DsarOutcomeView,
  type DsarRequestView,
  type DsarTaskView,
} from './api';
import styles from './Dsar.module.css';
import { ProblemBanner } from './ProblemBanner';
import { OutcomeKindPill, TaskStatusPill } from './StatusPills';

/**
 * Fan-out status per module: one row per module task with its outcomes, a retry for a failed or NOT_BUILT task and
 * the DPO's per-task decisions (redact third-party data, legal hold). A NOT_BUILT task blocks completion.
 */
export function TasksPanel({
  request,
  tasks,
}: {
  request: DsarRequestView;
  tasks: DsarTaskView[];
}) {
  const { t } = useTranslation('compliance');
  const { keyFor, release } = useIdempotencyKey();
  const run = useDsarCommand();
  const [redact, setRedact] = useState<Record<string, boolean>>({});
  const open = request.status !== 'COMPLETED' && request.status !== 'REFUSED';

  const send = (
    name: 'retry-task' | 'decide-item',
    body: Record<string, unknown>,
    done: string,
  ) => {
    const command = { name, body };
    run.mutate(
      { command, key: keyFor(command) },
      {
        onSuccess: () => {
          release();
          toast.success({ title: t(done), description: request.dsarId });
        },
      },
    );
  };

  if (tasks.length === 0) return <p className="ds-caption">{t('dsar.tasks.none')}</p>;
  return (
    <div className={styles.stack}>
      <div className={styles.scroll}>
        <table className={styles.grid} aria-label={t('dsar.tasks.title')}>
          <thead>
            <tr>
              <th scope="col">{t('dsar.tasks.module')}</th>
              <th scope="col">{t('dsar.tasks.status')}</th>
              <th scope="col">{t('dsar.tasks.phase')}</th>
              <th scope="col">{t('dsar.tasks.attempts')}</th>
              <th scope="col">{t('dsar.tasks.detail')}</th>
              <th scope="col">{t('dsar.tasks.actions')}</th>
            </tr>
          </thead>
          <tbody>
            {tasks.map((task) => (
              <tr key={task.taskId}>
                <td className="ds-mono">{task.module}</td>
                <td>
                  <TaskStatusPill status={task.status} />
                </td>
                <td>{task.phase}</td>
                <td>{task.attempts}</td>
                <td>
                  {task.error ?? (task.status === 'NOT_BUILT' ? t('dsar.tasks.notBuilt') : '—')}
                </td>
                <td>
                  {open ? (
                    <div className={cx(styles.actions)}>
                      {task.status === 'FAILED' || task.status === 'NOT_BUILT' ? (
                        <Button
                          variant="secondary"
                          isDisabled={run.isPending}
                          onPress={() => {
                            send(
                              'retry-task',
                              { id: request.dsarId, taskId: task.taskId },
                              'dsar.done.retry',
                            );
                          }}
                        >
                          {t('dsar.tasks.retry')}
                        </Button>
                      ) : null}
                      <Checkbox
                        isSelected={redact[task.taskId] ?? task.redactThirdParty ?? false}
                        onChange={(value) => {
                          setRedact((prev) => ({ ...prev, [task.taskId]: value }));
                        }}
                      >
                        {t('dsar.tasks.redact')}
                      </Checkbox>
                      <Button
                        variant="ghost"
                        isDisabled={run.isPending}
                        onPress={() => {
                          send(
                            'decide-item',
                            {
                              id: request.dsarId,
                              taskId: task.taskId,
                              redactThirdParty:
                                redact[task.taskId] ?? task.redactThirdParty ?? false,
                            },
                            'dsar.done.decide',
                          );
                        }}
                      >
                        {t('dsar.tasks.decide')}
                      </Button>
                    </div>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {run.isError ? <ProblemBanner error={run.error} /> : null}
    </div>
  );
}

/** The per-record-class outcomes of every task (what was found, erased, anonymised, restricted or not built). */
export function OutcomesTable({ outcomes }: { outcomes: DsarOutcomeView[] }) {
  const { t } = useTranslation('compliance');
  if (outcomes.length === 0) return <p className="ds-caption">{t('dsar.outcomes.none')}</p>;
  return (
    <div className={styles.scroll}>
      <table className={styles.grid} aria-label={t('dsar.outcomes.title')}>
        <thead>
          <tr>
            <th scope="col">{t('dsar.outcomes.module')}</th>
            <th scope="col">{t('dsar.outcomes.recordClass')}</th>
            <th scope="col">{t('dsar.outcomes.kind')}</th>
            <th scope="col">{t('dsar.outcomes.count')}</th>
            <th scope="col">{t('dsar.outcomes.legalBasis')}</th>
            <th scope="col">{t('dsar.outcomes.retentionEnd')}</th>
          </tr>
        </thead>
        <tbody>
          {outcomes.map((o, index) => (
            <tr key={`${o.module}-${o.recordClass}-${String(index)}`}>
              <td className="ds-mono">{o.module}</td>
              <td>{o.recordClass}</td>
              <td>{o.kind ? <OutcomeKindPill kind={o.kind} /> : '—'}</td>
              <td>{o.recordCount}</td>
              <td>{o.legalBasis ?? '—'}</td>
              <td>{o.retentionEnd ?? '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
