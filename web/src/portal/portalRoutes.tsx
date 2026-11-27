import type { RouteObject } from 'react-router';

import { PortalGuard } from './PortalGuard';
import { PortalShell } from './PortalShell';
import { ComingSoonPage, NotFoundPage } from './pages/ComingSoonPage';
import { DashboardPage } from './pages/DashboardPage';
import { WithdrawalPage } from './pages/WithdrawalPage';

/**
 * The customer portal route tree (BO-CHN-12), mounted at `/portal` in the root `routes.tsx` OUTSIDE the staff
 * AppShell. `PortalShell` frames every portal screen; `PortalGuard` (role `Customer.Portal`) protects all of them
 * except the public withdrawal declaration (open decision #2).
 * BO-CHN-13 replaces the `ComingSoonPage` stubs with the real pages inside the `PortalGuard` children.
 */
export const portalRoutes: RouteObject[] = [
  {
    path: '/portal',
    element: <PortalShell />,
    children: [
      { path: 'withdrawal', element: <WithdrawalPage /> },
      {
        element: <PortalGuard />,
        children: [
          { index: true, element: <DashboardPage /> },
          { path: 'policies/*', element: <ComingSoonPage area="policies" /> },
          { path: 'billing/*', element: <ComingSoonPage area="billing" /> },
          { path: 'documents/*', element: <ComingSoonPage area="documents" /> },
          { path: 'messages/*', element: <ComingSoonPage area="messages" /> },
          { path: 'notifications/*', element: <ComingSoonPage area="notifications" /> },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
];
