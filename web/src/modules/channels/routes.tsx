import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { withdrawalRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  // The customer withdrawal declaration moved to /portal/withdrawal (BO-CHN-12); the root routes redirect the old path.
  {
    path: 'channels/withdrawals',
    element: <RequireRole roles={withdrawalRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../channels/withdrawal/WithdrawalListPage')).WithdrawalListPage,
        }),
      },
      {
        path: ':id',
        lazy: async () => ({
          Component: (await import('../channels/withdrawal/WithdrawalDetailPage'))
            .WithdrawalDetailPage,
        }),
      },
    ],
  },
];

export default {
  order: 60,
  routes,
  implemented: ['/channels/withdrawals'],
} satisfies ModuleRoutes;
