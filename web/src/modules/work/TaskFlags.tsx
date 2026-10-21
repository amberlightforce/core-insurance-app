import { useTranslation } from 'react-i18next';

import { StatusPill } from '../../design-system';
import type { Task } from './api';

/** Priority and SLA state of a task as pills: only what needs attention is shown. */
export function TaskFlags({ task }: { task: Task }) {
  const { t } = useTranslation('work');
  return (
    <>
      {task.slaBreached && task.state === 'OPEN' ? (
        <StatusPill semantic="breached" announceChanges={false} />
      ) : null}
      {task.priority === 'HIGH' ? (
        <StatusPill semantic="warning" text={t('priority.HIGH')} announceChanges={false} />
      ) : null}
    </>
  );
}
