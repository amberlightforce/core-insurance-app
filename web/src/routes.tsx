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

const implemented = new Set([
  '/parties',
  '/policies',
  '/billing',
  '/finance',
  '/claims',
  '/underwriting',
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
