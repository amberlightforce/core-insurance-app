import type { RouteObject } from 'react-router';

import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'parties',
    lazy: async () => ({
      Component: (await import('../party/PartySearchPage')).PartySearchPage,
    }),
  },
  {
    path: 'parties/new',
    lazy: async () => ({
      Component: (await import('../party/PartyCreatePage')).PartyCreatePage,
    }),
  },
  {
    path: 'parties/:partyId',
    lazy: async () => ({
      Component: (await import('../party/PartyViewPage')).PartyViewPage,
    }),
  },
];

export default {
  order: 30,
  routes,
  implemented: ['/parties'],
} satisfies ModuleRoutes;
