import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { underwritingRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const loadWorkbench = async () => ({
  Component: (await import('./workbench/WorkbenchPage')).WorkbenchPage,
});

const routes: RouteObject[] = [
  {
    path: 'policies/referrals',
    element: <RequireRole roles={underwritingRoles} />,
    children: [{ index: true, lazy: loadWorkbench }],
  },
  {
    // The «Ανάληψη κινδύνου» rail entry opens the same referral workbench (SL5-UI-UW-WB).
    path: 'underwriting',
    element: <RequireRole roles={underwritingRoles} />,
    children: [{ index: true, lazy: loadWorkbench }],
  },
];

export default {
  order: 50,
  routes,
  implemented: ['/underwriting'],
} satisfies ModuleRoutes;
