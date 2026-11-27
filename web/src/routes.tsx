import { Navigate, type RouteObject } from 'react-router';

import { AppLayout, ModulePlaceholder } from './App';
import { collectModules, type ModuleRoutes } from './app-shell/moduleRegistry';
import { adminNavItem, defaultNavItems } from './app-shell/navigation';
import { StaffGate } from './auth/StaffGate';
import { DevSignIn } from './dev-auth/DevSignIn';
import { HomePage } from './modules/home/HomePage';
import { portalRoutes } from './portal/portalRoutes';

const routeModules = collectModules(
  import.meta.glob<{ default: ModuleRoutes }>('./modules/*/routes.tsx', { eager: true }),
);

/** Module routes load on demand (D-FE-23): each module contributes its own `routes.tsx` (BO-WEB-01). */
const moduleRoutes: RouteObject[] = routeModules.flatMap((module) => module.routes);

const implemented = new Set(routeModules.flatMap((module) => module.implemented ?? []));

/** Placeholder routes for modules whose work packages have not landed, plus the thin-slice staff screens. */
export const routes: RouteObject[] = [
  // Development-only local sign-in (D-SLC-03), full-bleed outside the shell; an empty state when the API does
  // not offer it.
  { path: '/dev/sign-in', element: <DevSignIn /> },
  // Customer portal (BO-CHN-12): its own shell, no staff imports; the staff shell below refuses customers.
  ...portalRoutes,
  // The public withdrawal declaration moved to the portal; the old address redirects (links, e-mails).
  { path: '/channels/withdrawal', element: <Navigate to="/portal/withdrawal" replace /> },
  {
    path: '/',
    element: (
      <StaffGate>
        <AppLayout />
      </StaffGate>
    ),
    children: [
      // Home is the first screen: loaded with the shell, never lazily (no blank frame while a chunk loads).
      { index: true, element: <HomePage /> },
      ...[...defaultNavItems.slice(1), adminNavItem]
        .filter((item) => !implemented.has(item.to))
        .map((item) => ({
          path: `${item.to.slice(1)}/*`,
          element: <ModulePlaceholder />,
        })),
      ...moduleRoutes,
      { path: '*', element: <ModulePlaceholder /> },
    ],
  },
];
