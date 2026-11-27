import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { packRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'admin/packs',
    element: <RequireRole roles={packRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../market/packs/PackRegistryPage')).PackRegistryPage,
        }),
      },
      {
        path: ':packId',
        lazy: async () => ({
          Component: (await import('../market/packs/PackDetailPage')).PackDetailPage,
        }),
      },
      {
        path: ':packId/activations/:activationId',
        lazy: async () => ({
          Component: (await import('../market/packs/ActivationPage')).ActivationPage,
        }),
      },
    ],
  },
];

export default {
  order: 150,
  routes,
  implemented: ['/admin/packs'],
} satisfies ModuleRoutes;
