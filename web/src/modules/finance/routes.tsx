import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { financeRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'finance',
    lazy: async () => ({
      Component: (await import('../finance/FinanceHomePage')).FinanceHomePage,
    }),
  },
  {
    path: 'finance/journals/policy/:policyNumber',
    lazy: async () => ({ Component: (await import('../finance/JournalPage')).JournalPage }),
  },
  {
    path: 'finance',
    element: <RequireRole roles={financeRoles} />,
    children: [
      {
        path: 'trial-balance',
        lazy: async () => ({
          Component: (await import('../finance/ops/TrialBalancePage')).TrialBalancePage,
        }),
      },
      {
        path: 'manual-journals',
        lazy: async () => ({
          Component: (await import('../finance/ops/ManualJournalsPage')).ManualJournalsPage,
        }),
      },
      {
        path: 'manual-journals/new',
        lazy: async () => ({
          Component: (await import('../finance/ops/ManualJournalFormPage')).ManualJournalFormPage,
        }),
      },
      {
        path: 'manual-journals/:id',
        lazy: async () => ({
          Component: (await import('../finance/ops/ManualJournalPage')).ManualJournalPage,
        }),
      },
      {
        path: 'intake-exceptions',
        lazy: async () => ({
          Component: (await import('../finance/ops/IntakeExceptionsPage')).IntakeExceptionsPage,
        }),
      },
      {
        path: 'period-close',
        lazy: async () => ({
          Component: (await import('../finance/ops/PeriodClosePage')).PeriodClosePage,
        }),
      },
      {
        path: 'earning',
        lazy: async () => ({
          Component: (await import('../finance/ops/EarningRunPage')).EarningRunPage,
        }),
      },
    ],
  },
];

export default {
  order: 100,
  routes,
  implemented: ['/finance'],
} satisfies ModuleRoutes;
