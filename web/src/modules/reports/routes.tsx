import type { RouteObject } from 'react-router';

import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'reports',
    lazy: async () => ({
      Component: (await import('../reports/ReportsPage')).ReportsPage,
    }),
  },
];

export default {
  order: 130,
  routes,
  implemented: ['/reports'],
} satisfies ModuleRoutes;
