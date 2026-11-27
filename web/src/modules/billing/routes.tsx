import type { RouteObject } from 'react-router';

import { RequireRole } from '../../auth/RequireRole';
import { billingRoles } from '../../auth/roles';
import type { ModuleRoutes } from '../../app-shell/moduleRegistry';

const routes: RouteObject[] = [
  {
    path: 'billing',
    lazy: async () => ({
      Component: (await import('../billing/BillingHomePage')).BillingHomePage,
    }),
  },
  {
    path: 'billing/accounts/:accountId',
    lazy: async () => ({ Component: (await import('../billing/AccountPage')).AccountPage }),
  },
  {
    path: 'billing/invoices/:invoiceId',
    lazy: async () => ({ Component: (await import('../billing/InvoicePage')).InvoicePage }),
  },
  {
    // SL4-UI-BIL: receivables and FS_CLEARING release approvals (billing roles; the guard is shared with refunds).
    path: 'billing/receivables',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../billing/receivables/ReceivablesListPage'))
            .ReceivablesListPage,
        }),
      },
      {
        path: ':receivableId',
        lazy: async () => ({
          Component: (await import('../billing/receivables/ReceivableDetailPage'))
            .ReceivableDetailPage,
        }),
      },
    ],
  },
  // NX-UI-14c: billing operations screens (billing roles).
  {
    path: 'billing/suspense',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../billing/ops/SuspensePage')).SuspensePage,
        }),
      },
    ],
  },
  {
    path: 'billing/plans',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../billing/ops/PlansPage')).PlansPage,
        }),
      },
    ],
  },
  {
    path: 'billing/mandates',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../billing/ops/MandatesPage')).MandatesPage,
        }),
      },
    ],
  },
  {
    path: 'billing/account-currents',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../billing/ops/AccountCurrentsPage')).AccountCurrentsPage,
        }),
      },
    ],
  },
  {
    // SL6-UI-BIL-DELINQ: delinquency cases (billing roles; guard shared with refunds).
    path: 'billing/delinquency',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../billing/delinquency/DelinquencyListPage'))
            .DelinquencyListPage,
        }),
      },
      {
        path: ':delinquencyId',
        lazy: async () => ({
          Component: (await import('../billing/delinquency/DelinquencyDetailPage'))
            .DelinquencyDetailPage,
        }),
      },
    ],
  },
  {
    path: 'billing/releases',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../billing/receivables/ReleasesInboxPage')).ReleasesInboxPage,
        }),
      },
      {
        path: ':disbursementId',
        lazy: async () => ({
          Component: (await import('../billing/receivables/ReleaseDetailPage')).ReleaseDetailPage,
        }),
      },
    ],
  },
  {
    // Refund screens: billing roles see them, everyone else the no-permission state.
    path: 'billing/refunds',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('../billing/refunds/RefundsInboxPage')).RefundsInboxPage,
        }),
      },
      {
        path: ':refundId',
        lazy: async () => ({
          Component: (await import('../billing/refunds/RefundDetailPage')).RefundDetailPage,
        }),
      },
    ],
  },
];

export default {
  order: 80,
  routes,
  implemented: ['/billing'],
} satisfies ModuleRoutes;
