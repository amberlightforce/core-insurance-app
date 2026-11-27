import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { claimsRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    // Claims screens: a claims role sees them, everyone else the no-permission state.
    path: 'claims',
    element: <RequireRole roles={claimsRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../claims/ClaimsHomePage')).ClaimsHomePage,
        }),
      },
      {
        path: 'new',
        lazy: async () => ({ Component: (await import('../claims/FnolPage')).FnolPage }),
      },
      {
        path: 'approvals',
        lazy: async () => ({
          Component: (await import('../claims/ApprovalsInboxPage')).ApprovalsInboxPage,
        }),
      },
      {
        path: 'approvals/:requestId',
        lazy: async () => ({
          Component: (await import('../claims/ApprovalDetailPage')).ApprovalDetailPage,
        }),
      },
      {
        path: 'fs-statements',
        lazy: async () => ({
          Component: (await import('../claims/fs-statements/FsStatementsPage')).FsStatementsPage,
        }),
      },
      {
        path: ':claimId',
        lazy: async () => ({
          Component: (await import('../claims/ClaimViewPage')).ClaimViewPage,
        }),
      },
    ],
  },
];

export default {
  order: 70,
  routes,
  implemented: ['/claims'],
} satisfies ModuleRoutes;
