import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { reinsuranceRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'reinsurance',
    element: <RequireRole roles={reinsuranceRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../reinsurance/ReinsuranceHomePage')).ReinsuranceHomePage,
        }),
      },
      {
        path: 'contracts/new',
        lazy: async () => ({
          Component: (await import('../reinsurance/ContractEditorPage')).NewContractPage,
        }),
      },
      {
        path: 'contracts/:contractId',
        lazy: async () => ({
          Component: (await import('../reinsurance/ContractPage')).ContractPage,
        }),
      },
      {
        path: 'contracts/:contractId/edit',
        lazy: async () => ({
          Component: (await import('../reinsurance/ContractEditorPage')).EditContractPage,
        }),
      },
      {
        path: 'claims/:claimId',
        lazy: async () => ({
          Component: (await import('../reinsurance/ClaimRecoveriesPage')).ClaimRecoveriesPage,
        }),
      },
    ],
  },
];

export default {
  order: 90,
  routes,
  implemented: ['/reinsurance'],
} satisfies ModuleRoutes;
