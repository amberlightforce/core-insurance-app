import type { ModuleIconName } from '../design-system/icons';
import { collectModules, type ModuleNav } from './moduleRegistry';

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

/**
 * Default staff navigation, collected from every `modules/<mod>/nav.ts` and ordered by its `order`
 * (role-configured order arrives with PLT; Part 1 §3.4).
 */
export const defaultNavItems: NavItem[] = collectModules(
  import.meta.glob<{ default: ModuleNav }>('../modules/*/nav.ts', { eager: true }),
).flatMap((module) => module.items);

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
