import type { RouteObject } from 'react-router';

import { AppLayout, ModulePlaceholder } from './App';
import { adminNavItem, defaultNavItems } from './app-shell/navigation';

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
      { path: '*', element: <ModulePlaceholder /> },
    ],
  },
];
