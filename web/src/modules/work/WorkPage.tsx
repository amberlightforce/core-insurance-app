import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import {
  Banner,
  Button,
  EmptyState,
  Tabs,
  announce,
  useTabSearchParam,
  type TabItem,
} from '../../design-system';
import { problemOf } from '../staff/problem';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import staff from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { OtherQueues } from './OtherQueues';
import {
  nextTaskOf,
  useCanSeeInbox,
  useClaimTask,
  useGetNextTask,
  useTaskDigest,
  useTasks,
  type Task,
  type TaskScope,
} from './api';
import { countLabel, objectLink, useQueueName, useTaskTitle } from './parts';
import { TaskFlags } from './TaskFlags';
import styles from './Work.module.css';

function TaskRow({ task, scope }: { task: Task; scope: TaskScope }) {
  const { t } = useTranslation('work');
  const fmt = useFormat();
  const title = useTaskTitle();
  const queueName = useQueueName();
  const navigate = useNavigate();
  const claim = useClaimTask();
  const { keyFor, release } = useIdempotencyKey();
  const [failed, setFailed] = useState<string | null>(null);
  const assignedTo = task.assignedToMe
    ? t('task.assignedToMe')
    : task.assignee.type === 'QUEUE'
      ? t('task.unassigned')
      : task.assignee.id;

  const takeIt = () => {
    setFailed(null);
    const body = { id: task.id, rulesOrMe: 'me' as const };
    return claim
      .mutateAsync({ body, key: keyFor(body) })
      .then(() => {
        release();
        announce(t('claim.done'));
        void navigate(`/work/tasks/${task.id}`);
      })
      .catch((error: unknown) => {
        const problem = problemOf(error);
        setFailed(problem.status === 409 ? t('claim.taken') : (problem.title ?? t('claim.failed')));
      });
  };

  return (
    <li className={styles.taskRow} data-testid="task-row">
      <div className={styles.taskMain}>
        <span className={styles.taskTitle}>
          {title(task)}
          <TaskFlags task={task} />
        </span>
        <span className={styles.taskMeta}>
          <span className="ds-mono">{task.number}</span>
          <span>{queueName(task.queueCode)}</span>
          {scope !== 'mine' ? <span>{assignedTo}</span> : null}
          {task.targetAt ? (
            <span>{t('task.due', { date: fmt.dateTime(task.targetAt) })}</span>
          ) : null}
          {objectLink(task.primaryObject) ? null : (
            <span className="ds-mono">
              {task.primaryObject.module}/{task.primaryObject.type}
            </span>
          )}
        </span>
        {failed ? <Banner variant="warning" live="status" title={failed} /> : null}
      </div>
      <div className={styles.taskActions}>
        {task.claimableByMe ? (
          <Button
            variant="secondary"
            size="sm"
            isLoading={claim.isPending}
            onPress={() => {
              void takeIt();
            }}
          >
            {t('task.claim')}
          </Button>
        ) : null}
        <LinkButton to={`/work/tasks/${task.id}`}>{t('task.open')}</LinkButton>
      </div>
    </li>
  );
}

function TaskList({ scope }: { scope: TaskScope }) {
  const { t } = useTranslation('work');
  const query = useTasks(scope);
  return (
    <QueryView query={query}>
      {({ tasks }) =>
        tasks.length === 0 ? (
          <EmptyState
            kind="done"
            headingLevel={3}
            headline={t(`tabs.empty.${scope}.title`)}
            description={t(`tabs.empty.${scope}.body`)}
          />
        ) : (
          <>
            <ul className={styles.taskList} aria-label={t(`tabs.${scope}`)}>
              {tasks.map((task) => (
                <TaskRow key={task.id} task={task} scope={scope} />
              ))}
            </ul>
            {query.hasNextPage ? (
              <Button
                variant="secondary"
                isLoading={query.isFetchingNextPage}
                onPress={() => {
                  void query.fetchNextPage();
                }}
              >
                {t('showMore')}
              </Button>
            ) : null}
          </>
        )
      }
    </QueryView>
  );
}

/** «Επόμενη εργασία»: wrk.Queue.getNext takes the most urgent open task of the caller's queues. */
function NextTaskButton({ onMessage }: { onMessage: (message: string | null) => void }) {
  const { t } = useTranslation('work');
  const navigate = useNavigate();
  const next = useGetNextTask();
  const { keyFor, release } = useIdempotencyKey();

  const pull = () => {
    onMessage(null);
    return next
      .mutateAsync({ key: keyFor({}) })
      .then((response) => {
        release();
        void navigate(`/work/tasks/${nextTaskOf(response).id}`);
      })
      .catch((error: unknown) => {
        release();
        const problem = problemOf(error);
        onMessage(
          problem.code?.endsWith('QUEUE-EMPTY') || problem.status === 404
            ? t('next.empty')
            : (problem.title ?? t('next.failed')),
        );
      });
  };

  return (
    <Button
      variant="primary"
      isLoading={next.isPending}
      onPress={() => {
        void pull();
      }}
    >
      {t('next.button')}
    </Button>
  );
}

function Inbox() {
  const { t } = useTranslation('work');
  const [tab, setTab] = useTabSearchParam('tab', 'mine');
  const [message, setMessage] = useState<string | null>(null);
  const mine = useTaskDigest('mine');
  const queue = useTaskDigest('queue');
  const digests = { mine: mine.data, queue: queue.data, team: undefined };
  const items: TabItem[] = (['mine', 'queue', 'team'] as const).map((id) => {
    const data = digests[id];
    const count = data ? countLabel(data.tasks.length, data.more) : undefined;
    return { id, label: t(`tabs.${id}`), ...(count !== undefined ? { count } : {}) };
  });

  return (
    <Section title={t('inbox.title')} actions={<NextTaskButton onMessage={setMessage} />}>
      {message ? <Banner variant="info" live="status" title={message} /> : null}
      <Tabs
        aria-label={t('inbox.title')}
        items={items}
        {...(tab ? { selectedKey: tab } : {})}
        onSelectionChange={setTab}
      >
        {(item) => (
          <TaskList scope={item.id === 'queue' || item.id === 'team' ? item.id : 'mine'} />
        )}
      </Tabs>
    </Section>
  );
}

/**
 * «Η δουλειά μου» (/work): the WRK task inbox (my tasks, the unassigned queue, the team's work, next task) for
 * the roles that see a work queue, then the older per-module queues as «Άλλες ουρές» so nothing is lost.
 * TODO(hardening): move the older queues onto WRK patterns; saved filters and bulk reassign (supervisor).
 */
export function WorkPage() {
  const { t } = useTranslation('work');
  const canSeeInbox = useCanSeeInbox();
  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('title')}
        description={t('lead')}
      />
      {canSeeInbox ? <Inbox /> : null}
      <OtherQueues hideWhenEmpty={canSeeInbox} />
    </div>
  );
}
