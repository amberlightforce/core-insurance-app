import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { productRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'products',
    element: <RequireRole roles={productRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../products/ProductCataloguePage')).ProductCataloguePage,
        }),
      },
      {
        path: ':code',
        lazy: async () => ({
          Component: (await import('../products/ProductDetailPage')).ProductDetailPage,
        }),
      },
    ],
  },
];

export default {
  order: 140,
  routes,
  implemented: ['/products'],
} satisfies ModuleRoutes;
