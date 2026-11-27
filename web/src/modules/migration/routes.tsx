import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { migrationRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'admin/migration',
    element: <RequireRole roles={migrationRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../migration/MigrationHomePage')).MigrationHomePage,
        }),
      },
      {
        path: 'batches/:batchId',
        lazy: async () => ({
          Component: (await import('../migration/BatchPage')).BatchPage,
        }),
      },
    ],
  },
];

export default {
  order: 160,
  routes,
  implemented: ['/admin/migration'],
} satisfies ModuleRoutes;
