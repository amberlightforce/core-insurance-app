import type { RouteObject } from 'react-router';

import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    // «My work»: the WRK task inbox plus the older per-module queues («Other queues»).
    path: 'work',
    lazy: async () => ({ Component: (await import('../work/WorkPage')).WorkPage }),
  },
  {
    path: 'work/tasks/:activityId',
    lazy: async () => ({
      Component: (await import('../work/TaskDetailPage')).TaskDetailPage,
    }),
  },
];

export default {
  order: 20,
  routes,
  implemented: ['/work'],
} satisfies ModuleRoutes;
