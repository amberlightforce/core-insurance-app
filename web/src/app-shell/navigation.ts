import type { ModuleIconName } from '../design-system/icons';

/** A rail destination. The label comes from `shell:nav.<id>`; the icon from the fixed module icons. */
export interface NavItem {
  id: ModuleIconName;
  to: string;
}

/** Default staff navigation (role-configured order arrives with PLT; Part 1 §3.4). */
export const defaultNavItems: NavItem[] = [
  { id: 'home', to: '/' },
  { id: 'work', to: '/work' },
  { id: 'parties', to: '/parties' },
  { id: 'policies', to: '/policies' },
  { id: 'underwriting', to: '/underwriting' },
  { id: 'claims', to: '/claims' },
  { id: 'billing', to: '/billing' },
  { id: 'reinsurance', to: '/reinsurance' },
  { id: 'finance', to: '/finance' },
  { id: 'documents', to: '/documents' },
  { id: 'compliance', to: '/compliance' },
  { id: 'reports', to: '/reports' },
  { id: 'products', to: '/products' },
];

/** Settings sits at the bottom of the rail. */
export const adminNavItem: NavItem = { id: 'admin', to: '/admin' };

/** Bottom tab bar shows at most 5 destinations plus «Περισσότερα» (Part 1 §3.5). */
export const bottomBarSlots = 4;

export function isActivePath(pathname: string, to: string): boolean {
  if (to === '/') return pathname === '/';
  return pathname === to || pathname.startsWith(`${to}/`);
}

export type Environment = { kind: 'uat' | 'dev'; name: string; shiftedDate?: string } | null;
