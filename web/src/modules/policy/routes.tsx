import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { packRollbackReviewerRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'policies',
    lazy: async () => ({
      Component: (await import('../policy/PoliciesHomePage')).PoliciesHomePage,
    }),
  },
  {
    path: 'policies/quotes/new',
    lazy: async () => ({
      Component: (await import('../quote/QuoteWizardPage')).QuoteWizardPage,
    }),
  },
  {
    path: 'policies/:policyId',
    lazy: async () => ({
      Component: (await import('../policy/PolicyViewPage')).PolicyViewPage,
    }),
  },
  {
    path: 'policies/:policyId/change',
    lazy: async () => ({
      Component: (await import('../policy/servicing/ChangeWorkspacePage')).ChangeWorkspacePage,
    }),
  },
  {
    path: 'policies/:policyId/cancel',
    lazy: async () => ({
      Component: (await import('../policy/servicing/CancellationPage')).CancellationPage,
    }),
  },
  {
    path: 'policies/:policyId/renew',
    lazy: async () => ({
      Component: (await import('../policy/servicing/RenewalPage')).RenewalPage,
    }),
  },
  {
    path: 'policies/pack-rollback',
    element: <RequireRole roles={packRollbackReviewerRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../policy/packRollback/ExceptionQueuePage')).ExceptionQueuePage,
        }),
      },
      {
        path: ':exceptionId',
        lazy: async () => ({
          Component: (await import('../policy/packRollback/ExceptionDetailPage'))
            .ExceptionDetailPage,
        }),
      },
    ],
  },
];

export default {
  order: 40,
  routes,
  implemented: ['/policies', '/policies/pack-rollback'],
} satisfies ModuleRoutes;
