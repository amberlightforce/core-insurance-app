import type { ModuleIconName } from '../design-system/icons';

/** A rail destination. The label comes from `shell:nav.<id>`; the icon from the fixed module icons. */
export interface NavItem {
  id: ModuleIconName;
  to: string;
  /** When set, only users holding one of these roles see the entry (the API still enforces permissions). */
  roles?: readonly string[];
}

/** Roles that work claims (dev users `claims` and `claimsmgr`). */
const claimsRoles = ['Staff.ClaimsHandler', 'Staff.ClaimsManager'] as const;

/** Roles that work underwriting referrals (permission uw.Referral.list; dev users `uwsenior`, `superuser`). */
const underwritingRoles = ['Staff.UnderwritingManager', 'Platform.Admin'] as const;

/** Default staff navigation (role-configured order arrives with PLT; Part 1 §3.4). */
export const defaultNavItems: NavItem[] = [
  { id: 'home', to: '/' },
  { id: 'work', to: '/work' },
  { id: 'parties', to: '/parties' },
  { id: 'policies', to: '/policies' },
  { id: 'underwriting', to: '/underwriting', roles: underwritingRoles },
  { id: 'claims', to: '/claims', roles: claimsRoles },
  { id: 'billing', to: '/billing' },
  {
    id: 'reinsurance',
    to: '/reinsurance',
    roles: [
      'Staff.ReinsuranceAccountant',
      'Staff.ReinsuranceManager',
      'Staff.RecoverySpecialist',
      'Platform.Admin',
    ],
  },
  { id: 'finance', to: '/finance' },
  { id: 'documents', to: '/documents' },
  { id: 'compliance', to: '/compliance' },
  { id: 'reports', to: '/reports' },
  { id: 'products', to: '/products' },
];

/** Entries without a `roles` restriction are visible to everyone; restricted ones need one of their roles. */
export function visibleNavItems(items: NavItem[], userRoles: readonly string[]): NavItem[] {
  return items.filter((item) => !item.roles || item.roles.some((role) => userRoles.includes(role)));
}

/** Settings sits at the bottom of the rail. */
export const adminNavItem: NavItem = { id: 'admin', to: '/admin' };

/** Bottom tab bar shows at most 5 destinations plus «Περισσότερα» (Part 1 §3.5). */
export const bottomBarSlots = 4;

export function isActivePath(pathname: string, to: string): boolean {
  if (to === '/') return pathname === '/';
  return pathname === to || pathname.startsWith(`${to}/`);
}

export type Environment = { kind: 'uat' | 'dev'; name: string; shiftedDate?: string } | null;
