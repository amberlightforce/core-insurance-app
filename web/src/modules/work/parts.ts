import { useTranslation } from 'react-i18next';

import type { ObjectRef, Task } from './api';

/** Greek title of a task by its pattern code; an unknown pattern shows the server's own title. */
export function useTaskTitle() {
  const { t } = useTranslation('work');
  return (task: Pick<Task, 'patternCode' | 'title'>) =>
    t(`patterns.${task.patternCode}`, { defaultValue: task.title });
}

/** Greek name of a queue by its code; an unknown queue shows the code. */
export function useQueueName() {
  const { t } = useTranslation('work');
  return (code: string) => t(`queues.${code}`, { defaultValue: code });
}

/** Where the task's primary object opens in the staff app, when the app has a screen for that object type. */
export function objectLink(object: ObjectRef): string | null {
  if (object.module === 'POL' && object.type === 'Policy') return `/policies/${object.id}`;
  if (object.module === 'MKT' && object.type === 'PackActivation') return '/policies/pack-rollback';
  return null;
}

/** «1 εκκρεμής» style counts for tab badges: capped like the server page («200+»). */
export function countLabel(count: number, more: boolean): number | undefined {
  return more ? undefined : count;
}
