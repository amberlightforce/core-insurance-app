import { type RouteObject } from 'react-router';

import { AppLayout, ModulePlaceholder } from './App';
import { adminNavItem, defaultNavItems } from './app-shell/navigation';
import { RequireRole } from './auth/RequireRole';
import {
  aiAdminRoles,
  billingRoles,
  claimsRoles,
  complianceRoles,
  dsarRoles,
  migrationRoles,
  packRoles,
  packRollbackReviewerRoles,
  reinsuranceRoles,
  underwritingRoles,
  withdrawalRoles,
} from './auth/roles';
import { DevSignIn } from './dev-auth/DevSignIn';
import { HomePage } from './modules/home/HomePage';

const loadWorkbench = async () => ({
  Component: (await import('./modules/underwriting/workbench/WorkbenchPage')).WorkbenchPage,
});

/** Module routes load on demand (D-FE-23): the staff screens of the thin slice. */
const moduleRoutes: RouteObject[] = [
  {
    // «My work»: the WRK task inbox plus the older per-module queues («Other queues»).
    path: 'work',
    lazy: async () => ({ Component: (await import('./modules/work/WorkPage')).WorkPage }),
  },
  {
    path: 'work/tasks/:activityId',
    lazy: async () => ({
      Component: (await import('./modules/work/TaskDetailPage')).TaskDetailPage,
    }),
  },
  {
    path: 'parties',
    lazy: async () => ({
      Component: (await import('./modules/party/PartySearchPage')).PartySearchPage,
    }),
  },
  {
    path: 'parties/new',
    lazy: async () => ({
      Component: (await import('./modules/party/PartyCreatePage')).PartyCreatePage,
    }),
  },
  {
    path: 'parties/:partyId',
    lazy: async () => ({
      Component: (await import('./modules/party/PartyViewPage')).PartyViewPage,
    }),
  },
  {
    path: 'policies',
    lazy: async () => ({
      Component: (await import('./modules/policy/PoliciesHomePage')).PoliciesHomePage,
    }),
  },
  {
    path: 'policies/quotes/new',
    lazy: async () => ({
      Component: (await import('./modules/quote/QuoteWizardPage')).QuoteWizardPage,
    }),
  },
  {
    path: 'policies/referrals',
    element: <RequireRole roles={underwritingRoles} />,
    children: [{ index: true, lazy: loadWorkbench }],
  },
  {
    // The «Ανάληψη κινδύνου» rail entry opens the same referral workbench (SL5-UI-UW-WB).
    path: 'underwriting',
    element: <RequireRole roles={underwritingRoles} />,
    children: [{ index: true, lazy: loadWorkbench }],
  },
  {
    path: 'policies/:policyId',
    lazy: async () => ({
      Component: (await import('./modules/policy/PolicyViewPage')).PolicyViewPage,
    }),
  },
  {
    path: 'policies/:policyId/change',
    lazy: async () => ({
      Component: (await import('./modules/policy/servicing/ChangeWorkspacePage'))
        .ChangeWorkspacePage,
    }),
  },
  {
    path: 'policies/:policyId/cancel',
    lazy: async () => ({
      Component: (await import('./modules/policy/servicing/CancellationPage')).CancellationPage,
    }),
  },
  {
    path: 'policies/:policyId/renew',
    lazy: async () => ({
      Component: (await import('./modules/policy/servicing/RenewalPage')).RenewalPage,
    }),
  },
  {
    path: 'billing',
    lazy: async () => ({
      Component: (await import('./modules/billing/BillingHomePage')).BillingHomePage,
    }),
  },
  {
    path: 'billing/accounts/:accountId',
    lazy: async () => ({ Component: (await import('./modules/billing/AccountPage')).AccountPage }),
  },
  {
    path: 'billing/invoices/:invoiceId',
    lazy: async () => ({ Component: (await import('./modules/billing/InvoicePage')).InvoicePage }),
  },
  {
    // SL4-UI-BIL: receivables and FS_CLEARING release approvals (billing roles; the guard is shared with refunds).
    path: 'billing/receivables',
    element: <RequireRole roles={billingRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('./modules/billing/receivables/ReceivablesListPage'))
            .ReceivablesListPage,
        }),
      },
      {
        path: ':receivableId',
        lazy: async () => ({
          Component: (await import('./modules/billing/receivables/ReceivableDetailPage'))
            .ReceivableDetailPage,
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
          Component: (await import('./modules/billing/delinquency/DelinquencyListPage'))
            .DelinquencyListPage,
        }),
      },
      {
        path: ':delinquencyId',
        lazy: async () => ({
          Component: (await import('./modules/billing/delinquency/DelinquencyDetailPage'))
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
          Component: (await import('./modules/billing/receivables/ReleasesInboxPage'))
            .ReleasesInboxPage,
        }),
      },
      {
        path: ':disbursementId',
        lazy: async () => ({
          Component: (await import('./modules/billing/receivables/ReleaseDetailPage'))
            .ReleaseDetailPage,
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
          Component: (await import('./modules/billing/refunds/RefundsInboxPage')).RefundsInboxPage,
        }),
      },
      {
        path: ':refundId',
        lazy: async () => ({
          Component: (await import('./modules/billing/refunds/RefundDetailPage')).RefundDetailPage,
        }),
      },
    ],
  },
  {
    // Claims screens: a claims role sees them, everyone else the no-permission state.
    path: 'claims',
    element: <RequireRole roles={claimsRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('./modules/claims/ClaimsHomePage')).ClaimsHomePage,
        }),
      },
      {
        path: 'new',
        lazy: async () => ({ Component: (await import('./modules/claims/FnolPage')).FnolPage }),
      },
      {
        path: 'approvals',
        lazy: async () => ({
          Component: (await import('./modules/claims/ApprovalsInboxPage')).ApprovalsInboxPage,
        }),
      },
      {
        path: 'approvals/:requestId',
        lazy: async () => ({
          Component: (await import('./modules/claims/ApprovalDetailPage')).ApprovalDetailPage,
        }),
      },
      {
        path: 'fs-statements',
        lazy: async () => ({
          Component: (await import('./modules/claims/fs-statements/FsStatementsPage'))
            .FsStatementsPage,
        }),
      },
      {
        path: ':claimId',
        lazy: async () => ({
          Component: (await import('./modules/claims/ClaimViewPage')).ClaimViewPage,
        }),
      },
    ],
  },
  {
    path: 'finance',
    lazy: async () => ({
      Component: (await import('./modules/finance/FinanceHomePage')).FinanceHomePage,
    }),
  },
  {
    path: 'finance/journals/policy/:policyNumber',
    lazy: async () => ({ Component: (await import('./modules/finance/JournalPage')).JournalPage }),
  },
];

moduleRoutes.push(
  {
    path: 'admin/packs',
    element: <RequireRole roles={packRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('./modules/market/packs/PackRegistryPage')).PackRegistryPage,
        }),
      },
      {
        path: ':packId',
        lazy: async () => ({
          Component: (await import('./modules/market/packs/PackDetailPage')).PackDetailPage,
        }),
      },
      {
        path: ':packId/activations/:activationId',
        lazy: async () => ({
          Component: (await import('./modules/market/packs/ActivationPage')).ActivationPage,
        }),
      },
    ],
  },
  {
    // AI control plane (SL6-UI-PLT-AI): AI administrators only.
    path: 'admin/ai',
    element: <RequireRole roles={aiAdminRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('./modules/platform/ai/AiControlPage')).AiControlPage,
        }),
      },
    ],
  },
  {
    path: 'policies/pack-rollback',
    element: <RequireRole roles={packRollbackReviewerRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('./modules/policy/packRollback/ExceptionQueuePage'))
            .ExceptionQueuePage,
        }),
      },
      {
        path: ':exceptionId',
        lazy: async () => ({
          Component: (await import('./modules/policy/packRollback/ExceptionDetailPage'))
            .ExceptionDetailPage,
        }),
      },
    ],
  },
);
moduleRoutes.push({
  path: 'reinsurance',
  element: <RequireRole roles={reinsuranceRoles} />,
  children: [
    {
      index: true,
      lazy: async () => ({
        Component: (await import('./modules/reinsurance/ReinsuranceHomePage')).ReinsuranceHomePage,
      }),
    },
    {
      path: 'contracts/new',
      lazy: async () => ({
        Component: (await import('./modules/reinsurance/ContractEditorPage')).NewContractPage,
      }),
    },
    {
      path: 'contracts/:contractId',
      lazy: async () => ({
        Component: (await import('./modules/reinsurance/ContractPage')).ContractPage,
      }),
    },
    {
      path: 'contracts/:contractId/edit',
      lazy: async () => ({
        Component: (await import('./modules/reinsurance/ContractEditorPage')).EditContractPage,
      }),
    },
    {
      path: 'claims/:claimId',
      lazy: async () => ({
        Component: (await import('./modules/reinsurance/ClaimRecoveriesPage')).ClaimRecoveriesPage,
      }),
    },
  ],
});

moduleRoutes.push({
  path: 'admin/migration',
  element: <RequireRole roles={migrationRoles} />,
  children: [
    {
      index: true,
      lazy: async () => ({
        Component: (await import('./modules/migration/MigrationHomePage')).MigrationHomePage,
      }),
    },
    {
      path: 'batches/:batchId',
      lazy: async () => ({
        Component: (await import('./modules/migration/BatchPage')).BatchPage,
      }),
    },
  ],
});

moduleRoutes.push({
  // NX-UI-09: complaints and statutory clocks landing; DSARs hang off it.
  path: 'compliance',
  element: <RequireRole roles={complianceRoles} />,
  children: [
    {
      index: true,
      lazy: async () => ({
        Component: (await import('./modules/compliance/home/ComplianceHomePage'))
          .ComplianceHomePage,
      }),
    },
  ],
});

moduleRoutes.push({
  path: 'compliance/dsar',
  element: <RequireRole roles={dsarRoles} />,
  children: [
    {
      index: true,
      lazy: async () => ({
        Component: (await import('./modules/compliance/dsar/DsarListPage')).DsarListPage,
      }),
    },
    {
      path: ':dsarId',
      lazy: async () => ({
        Component: (await import('./modules/compliance/dsar/DsarCasePage')).DsarCasePage,
      }),
    },
  ],
});

moduleRoutes.push(
  {
    // Customer withdrawal declaration + confirmation (SL6-UI-CHN-WITHDRAW).
    path: 'channels/withdrawal',
    lazy: async () => ({
      Component: (await import('./modules/channels/withdrawal/WithdrawalDeclarePage'))
        .WithdrawalDeclarePage,
    }),
  },
  {
    path: 'channels/withdrawals',
    element: <RequireRole roles={withdrawalRoles} />,
    children: [
      {
        index: true,
        lazy: async () => ({
          Component: (await import('./modules/channels/withdrawal/WithdrawalListPage'))
            .WithdrawalListPage,
        }),
      },
      {
        path: ':id',
        lazy: async () => ({
          Component: (await import('./modules/channels/withdrawal/WithdrawalDetailPage'))
            .WithdrawalDetailPage,
        }),
      },
    ],
  },
);

const implemented = new Set([
  '/work',
  '/admin/packs',
  '/admin/migration',
  '/compliance',
  '/compliance/dsar',
  '/channels/withdrawals',
  '/policies/pack-rollback',
  '/parties',
  '/policies',
  '/billing',
  '/finance',
  '/claims',
  '/underwriting',
  '/reinsurance',
]);

/** Placeholder routes for modules whose work packages have not landed, plus the thin-slice staff screens. */
export const routes: RouteObject[] = [
  {
    path: '/',
    element: <AppLayout />,
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
      // Development-only local sign-in (D-SLC-03); shows an empty state when the API does not offer it.
      { path: 'dev/sign-in', element: <DevSignIn /> },
      { path: '*', element: <ModulePlaceholder /> },
    ],
  },
];
