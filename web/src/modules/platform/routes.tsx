import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { aiAdminRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    // AI control plane (SL6-UI-PLT-AI): AI administrators only.
    path: 'admin/ai',
    element: <RequireRole roles={aiAdminRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../platform/ai/AiControlPage')).AiControlPage,
        }),
      },
    ],
  },
];

export default {
  order: 170,
  routes,
  implemented: [],
} satisfies ModuleRoutes;
