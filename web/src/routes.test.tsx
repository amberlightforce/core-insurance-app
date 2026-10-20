import { isValidElement } from 'react';
import type { RouteObject } from 'react-router';
import { describe, expect, it } from 'vitest';

import { adminNavItem, defaultNavItems } from './app-shell/navigation';
import { RequireRole } from './auth/RequireRole';
import { underwritingRoles } from './auth/roles';
import { routes } from './routes';

interface Flat {
  path: string;
  roles: readonly string[] | undefined;
}

/** Flattens the route tree to full paths with the roles of the nearest `RequireRole` ancestor (or the route itself). */
function flatten(
  list: RouteObject[],
  prefix: string,
  inherited: readonly string[] | undefined,
): Flat[] {
  return list.flatMap((route) => {
    const own =
      isValidElement<{ roles: readonly string[] }>(route.element) &&
      route.element.type === RequireRole
        ? route.element.props.roles
        : undefined;
    const roles = own ?? inherited;
    const segment = route.path ?? '';
    const path = (segment.startsWith('/') ? segment : `${prefix}/${segment}`).replace(/\/+$/u, '');
    const self: Flat[] =
      route.path === undefined && !route.index ? [] : [{ path: path || '/', roles }];
    return [...self, ...flatten(route.children ?? [], path, roles)];
  });
}

const flat = flatten(routes, '', undefined);
const rolesOf = (path: string) => flat.find((r) => r.path === path)?.roles;
const sorted = (roles: readonly string[] | undefined) => [...(roles ?? [])].sort();

describe('route guards vs navigation', () => {
  const navItems = [...defaultNavItems, adminNavItem];

  it('guards every role-restricted nav entry with exactly the same roles', () => {
    const restricted = navItems.filter((item) => item.roles);
    expect(restricted.length).toBeGreaterThan(5);
    for (const item of restricted) {
      expect(sorted(rolesOf(item.to)), `route ${item.to}`).toEqual(sorted(item.roles));
    }
  });

  it('does not hide nav entries whose route is guarded', () => {
    for (const item of navItems) {
      const guarded = flat.find((r) => r.path === item.to)?.roles;
      if (guarded) expect(item.roles, `nav ${item.to}`).toBeDefined();
    }
  });

  it('guards the underwriting workbench on both of its routes', () => {
    expect(sorted(rolesOf('/underwriting'))).toEqual(sorted(underwritingRoles));
    expect(sorted(rolesOf('/policies/referrals'))).toEqual(sorted(underwritingRoles));
  });

  it('keeps the /work page unguarded and /compliance redirecting to the DSAR console', () => {
    expect(flat.some((r) => r.path === '/work')).toBe(true);
    expect(rolesOf('/work')).toBeUndefined();
    expect(flat.some((r) => r.path === '/compliance')).toBe(true);
    expect(rolesOf('/compliance/dsar')).toBeDefined();
  });
});
