import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { withdrawalRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    // Customer withdrawal declaration + confirmation (SL6-UI-CHN-WITHDRAW).
    path: 'channels/withdrawal',
    lazy: async () => ({
      Component: (await import('../channels/withdrawal/WithdrawalDeclarePage'))
        .WithdrawalDeclarePage,
    }),
  },
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
