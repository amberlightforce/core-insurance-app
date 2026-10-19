import type { RouteObject } from 'react-router';

import { AppLayout, ModulePlaceholder } from './App';
import { adminNavItem, defaultNavItems } from './app-shell/navigation';
import { DevSignIn } from './dev-auth/DevSignIn';
import { HomePage } from './modules/home/HomePage';

/** Module routes load on demand (D-FE-23): the staff screens of the thin slice. */
const moduleRoutes: RouteObject[] = [
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
    lazy: async () => ({
      Component: (await import('./modules/underwriting/workbench/WorkbenchPage')).WorkbenchPage,
    }),
  },
  {
    // The «Ανάληψη κινδύνου» rail entry opens the same referral workbench (SL5-UI-UW-WB).
    path: 'underwriting',
    lazy: async () => ({
      Component: (await import('./modules/underwriting/workbench/WorkbenchPage')).WorkbenchPage,
    }),
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
    lazy: async () => ({
      Component: (await import('./modules/billing/refunds/RequireRefundRole')).RequireRefundRole,
    }),
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
    lazy: async () => ({
      Component: (await import('./modules/billing/refunds/RequireRefundRole')).RequireRefundRole,
    }),
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
    lazy: async () => ({
      Component: (await import('./modules/billing/refunds/RequireRefundRole')).RequireRefundRole,
    }),
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
    // Refund screens: billing roles see them, everyone else the no-permission state (RequireRefundRole).
    path: 'billing/refunds',
    lazy: async () => ({
      Component: (await import('./modules/billing/refunds/RequireRefundRole')).RequireRefundRole,
    }),
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
    // Claims screens: a claims role sees them, everyone else the no-permission state (RequireClaimsRole).
    path: 'claims',
    lazy: async () => ({
      Component: (await import('./modules/claims/RequireClaimsRole')).RequireClaimsRole,
    }),
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
    lazy: async () => ({
      Component: (await import('./modules/market/packs/RequirePackRole')).RequirePackRole,
    }),
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
    // AI control plane (SL6-UI-PLT-AI): AI administrators only (RequireAiRole).
    path: 'admin/ai',
    lazy: async () => ({
      Component: (await import('./modules/platform/ai/RequireAiRole')).RequireAiRole,
    }),
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
    lazy: async () => ({
      Component: (await import('./modules/policy/packRollback/RequireReviewerRole'))
        .RequireReviewerRole,
    }),
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
  lazy: async () => ({
    Component: (await import('./modules/reinsurance/RequireReinsuranceRole'))
      .RequireReinsuranceRole,
  }),
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
  lazy: async () => ({
    Component: (await import('./modules/migration/RequireMigrationRole')).RequireMigrationRole,
  }),
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
    lazy: async () => ({
      Component: (await import('./modules/channels/withdrawal/RequireWithdrawalRole'))
        .RequireWithdrawalRole,
    }),
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
  '/admin/packs',
  '/admin/migration',
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
