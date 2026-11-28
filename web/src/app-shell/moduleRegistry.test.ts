import { describe, expect, it } from 'vitest';

import { collectModules, type ModuleRoutes } from './moduleRegistry';
import { defaultNavItems } from './navigation';

describe('module registry', () => {
  it('orders discovered module files by their order number', () => {
    const modules = collectModules<ModuleRoutes>({
      '../modules/b/routes.tsx': { default: { order: 20, routes: [{ path: 'b' }] } },
      '../modules/fake/routes.tsx': { default: { order: 5, routes: [{ path: 'fake' }] } },
      '../modules/a/routes.tsx': { default: { order: 10, routes: [{ path: 'a' }] } },
    });
    expect(modules.flatMap((m) => m.routes.map((r) => r.path))).toEqual(['fake', 'a', 'b']);
  });

  it('discovers the real module nav files in the established rail order', () => {
    expect(defaultNavItems.map((item) => item.to)).toEqual([
      '/',
      '/work',
      '/parties',
      '/policies',
      '/underwriting',
      '/policies/pack-rollback',
      '/channels/withdrawals',
      '/claims',
      '/claims/fs-statements',
      '/billing',
      '/reinsurance',
      '/finance',
      '/documents',
      '/compliance',
      '/compliance/complaints',
      '/compliance/dsar',
      '/reports',
      '/products',
      '/admin/packs',
      '/admin/migration',
      '/admin/ai',
    ]);
  });
});
