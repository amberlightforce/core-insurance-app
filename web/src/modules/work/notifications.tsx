import { AlarmClockOff, ClipboardList } from 'lucide-react';
import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import type { NotificationItem } from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { useFormat } from '../staff/useFormat';
import { useCanSeeInbox, useTaskDigest } from './api';
import { useTaskTitle } from './parts';

/**
 * What the shell bell and notification centre show for the WRK inbox: each of my open tasks, plus a summary row when
 * unassigned queue work is waiting. Unread = open and mine. Nothing else feeds the centre yet.
 * TODO(hardening): server-side notifications (PRD-13 NTF) with read state; this derives from the task list.
 */
export function useWorkNotifications(): {
  items: NotificationItem[];
  count: number;
  attention: boolean;
} {
  const { t } = useTranslation('work');
  const fmt = useFormat();
  const title = useTaskTitle();
  const enabled = useCanSeeInbox();
  const mine = useTaskDigest('mine', enabled);
  const queue = useTaskDigest('queue', enabled);

  return useMemo(() => {
    const own = mine.data?.tasks ?? [];
    const waiting = queue.data?.tasks.length ?? 0;
    const items: NotificationItem[] = own.map((task) => ({
      id: `task-${task.id}`,
      title: title(task),
      body: task.targetAt ? t('task.due', { date: fmt.dateTime(task.targetAt) }) : task.number,
      time: fmt.dateTime(task.createdAt),
      unread: true,
      family: task.slaBreached ? 'danger' : task.priority === 'HIGH' ? 'warning' : 'info',
      icon: task.slaBreached ? AlarmClockOff : ClipboardList,
      actions: <LinkButton to={`/work/tasks/${task.id}`}>{t('task.open')}</LinkButton>,
    }));
    if (waiting > 0) {
      items.push({
        id: 'queue-waiting',
        title: t('notifications.queueWaiting', { count: waiting }),
        time: '',
        unread: false,
        family: 'neutral',
        icon: ClipboardList,
        actions: <LinkButton to="/work?tab=queue">{t('notifications.openQueue')}</LinkButton>,
      });
    }
    return { items, count: own.length, attention: own.some((task) => task.slaBreached) };
  }, [mine.data, queue.data, fmt, t, title]);
}
