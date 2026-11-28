import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { complianceRoles, fiscalRoles, dsarRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    // NX-UI-09: complaints and statutory clocks landing; DSARs hang off it.
    path: 'compliance',
    element: <RequireRole roles={complianceRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../compliance/home/ComplianceHomePage')).ComplianceHomePage,
        }),
      },
    ],
  },
  {
    // NX-UI-11: fiscal documents, reconciliation breaks and the AI-system register.
    path: 'compliance/fiscal',
    element: <RequireRole roles={fiscalRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../compliance/fiscal/FiscalListPage')).FiscalListPage,
        }),
      },
      {
        path: ':id',
        lazy: async () => ({
          Component: (await import('../compliance/fiscal/FiscalDocumentPage')).FiscalDocumentPage,
        }),
      },
    ],
  },
  {
    // BO-CMP-14: regulatory submissions workbench (BO-CMP-09).
    path: 'compliance/submissions',
    element: <RequireRole roles={complianceRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../compliance/submissions/SubmissionsListPage'))
            .SubmissionsListPage,
        }),
      },
      {
        path: ':id',
        lazy: async () => ({
          Component: (await import('../compliance/submissions/SubmissionPage')).SubmissionPage,
        }),
      },
    ],
  },
  {
    path: 'compliance/ai-systems',
    element: <RequireRole roles={complianceRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../compliance/ai/AiRegisterPage')).AiRegisterPage,
        }),
      },
    ],
  },
  {
    path: 'compliance/dsar',
    element: <RequireRole roles={dsarRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../compliance/dsar/DsarListPage')).DsarListPage,
        }),
      },
      {
        path: ':dsarId',
        lazy: async () => ({
          Component: (await import('../compliance/dsar/DsarCasePage')).DsarCasePage,
        }),
      },
    ],
  },
];

export default {
  order: 120,
  routes,
  implemented: [
    '/compliance',
    '/compliance/dsar',
    '/compliance/fiscal',
    '/compliance/ai-systems',
    '/compliance/submissions',
  ],
} satisfies ModuleRoutes;
