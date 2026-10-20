import {
  aiAdminRoles,
  claimsRoles,
  complianceRoles,
  dsarRoles,
  migrationRoles,
  packRoles,
  reinsuranceRoles,
  underwritingRoles,
  withdrawalRoles,
} from '../auth/roles';
import type { ModuleIconName } from '../design-system/icons';

/** A rail destination. The label comes from `shell:nav.<id>`; the icon from the fixed module icons. */
export interface NavItem {
  id: ModuleIconName;
  to: string;
  /** Overrides the `shell:nav.<key>` label key (default: the id). */
  label?: string;
  /** Overrides the icon (default: the id's icon). */
  icon?: ModuleIconName;
  /** When set, only users holding one of these roles see the entry (the API still enforces permissions). */
  roles?: readonly string[];
}

export {
  aiAdminRoles,
  claimsRoles,
  complianceRoles,
  dsarRoles,
  migrationRoles,
  packRoles,
  reinsuranceRoles,
  underwritingRoles,
  withdrawalRoles,
} from '../auth/roles';

/** Default staff navigation (role-configured order arrives with PLT; Part 1 §3.4). */
export const defaultNavItems: NavItem[] = [
  { id: 'home', to: '/' },
  { id: 'work', to: '/work' },
  { id: 'parties', to: '/parties' },
  { id: 'policies', to: '/policies' },
  { id: 'underwriting', to: '/underwriting', roles: underwritingRoles },
  {
    id: 'policies',
    to: '/policies/pack-rollback',
    label: 'packRollback',
    roles: underwritingRoles,
  },
  { id: 'policies', to: '/channels/withdrawals', label: 'withdrawals', roles: withdrawalRoles },
  { id: 'claims', to: '/claims', roles: claimsRoles },
  { id: 'claims', to: '/claims/fs-statements', label: 'fsStatements', roles: claimsRoles },
  { id: 'billing', to: '/billing' },
  {
    id: 'reinsurance',
    to: '/reinsurance',
    roles: reinsuranceRoles,
  },
  { id: 'finance', to: '/finance' },
  { id: 'documents', to: '/documents' },
  { id: 'compliance', to: '/compliance', roles: complianceRoles },
  {
    id: 'compliance',
    to: '/compliance/dsar',
    label: 'dsar',
    roles: dsarRoles,
  },
  { id: 'reports', to: '/reports' },
  { id: 'products', to: '/products' },
  { id: 'admin', to: '/admin/packs', label: 'packs', roles: packRoles },
  { id: 'admin', to: '/admin/migration', label: 'migration', roles: migrationRoles },
  {
    id: 'admin',
    to: '/admin/ai',
    label: 'aiControl',
    roles: aiAdminRoles,
  },
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
