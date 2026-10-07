import type { RouteObject } from 'react-router';

import { AppLayout, ModulePlaceholder } from './App';
import { adminNavItem, defaultNavItems } from './app-shell/navigation';
import { DevSignIn } from './dev-auth/DevSignIn';

/** Placeholder routes per module until the module work packages add their own route trees. */
export const routes: RouteObject[] = [
  {
    path: '/',
    element: <AppLayout />,
    children: [
      { index: true, element: <ModulePlaceholder /> },
      ...[...defaultNavItems.slice(1), adminNavItem].map((item) => ({
        path: `${item.to.slice(1)}/*`,
        element: <ModulePlaceholder />,
      })),
      // Development-only local sign-in (D-SLC-03); shows an empty state when the API does not offer it.
      { path: 'dev/sign-in', element: <DevSignIn /> },
      { path: '*', element: <ModulePlaceholder /> },
    ],
  },
];
