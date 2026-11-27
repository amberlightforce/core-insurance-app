import type { RouteObject } from 'react-router';

import { PortalGuard } from './PortalGuard';
import { PortalShell } from './PortalShell';
import { BillingPage } from './pages/BillingPage';
import { CancellationPage } from './pages/CancellationPage';
import { DashboardPage } from './pages/DashboardPage';
import { DocumentsPage } from './pages/DocumentsPage';
import { MessagesPage } from './pages/MessagesPage';
import { NotFoundPage } from './pages/NotFoundPage';
import { NotificationsPage } from './pages/NotificationsPage';
import { PoliciesPage } from './pages/PoliciesPage';
import { PolicyDetailPage } from './pages/PolicyDetailPage';
import { ProfilePage } from './pages/ProfilePage';
import { RenewalPage } from './pages/RenewalPage';
import { WithdrawalPage } from './pages/WithdrawalPage';

/**
 * The customer portal route tree (BO-CHN-12), mounted at `/portal` in the root `routes.tsx` OUTSIDE the staff
 * AppShell. `PortalShell` frames every portal screen; `PortalGuard` (role `Customer.Portal`) protects all of them
 * except the public withdrawal declaration (open decision #2).
 * The pages (BO-CHN-13) live in `pages/`: overview, policies (list, detail, renew, cancel), payments, documents,
 * messages, notifications and the profile (contact data).
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
          { path: 'policies', element: <PoliciesPage /> },
          { path: 'policies/:id', element: <PolicyDetailPage /> },
          { path: 'policies/:id/renew', element: <RenewalPage /> },
          { path: 'policies/:id/cancel', element: <CancellationPage /> },
          { path: 'billing', element: <BillingPage /> },
          { path: 'documents', element: <DocumentsPage /> },
          { path: 'messages', element: <MessagesPage /> },
          { path: 'notifications', element: <NotificationsPage /> },
          { path: 'profile', element: <ProfilePage /> },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
];
