import type { RouteObject } from 'react-router';

import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'documents',
    lazy: async () => ({
      Component: (await import('../documents/DocumentsHomePage')).DocumentsHomePage,
    }),
  },
  {
    path: 'documents/subject/:module/:type/:id',
    lazy: async () => ({
      Component: (await import('../documents/SubjectDocumentsPage')).SubjectDocumentsPage,
    }),
  },
  {
    path: 'documents/:documentId',
    lazy: async () => ({
      Component: (await import('../documents/DocumentDetailPage')).DocumentDetailPage,
    }),
  },
];

export default {
  order: 110,
  routes,
  implemented: ['/documents'],
} satisfies ModuleRoutes;
