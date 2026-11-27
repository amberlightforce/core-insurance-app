import type { RouteObject } from 'react-router';

import type { NavItem } from './navigation';

/**
 * A module's staff routes, exported as the default of `modules/<mod>/routes.tsx`. Adding a module means adding
 * that file (and optionally `nav.ts`); the root `routes.tsx` / `navigation.ts` never need editing (BO-WEB-01).
 */
export interface ModuleRoutes {
  /** Sort key across modules (ascending); keep gaps of 10 so modules can slot in between. */
  order: number;
  routes: RouteObject[];
  /** Rail destinations (`NavItem.to`) this module really implements; the rest get `ModulePlaceholder`. */
  implemented?: string[];
}

/** A module's rail entries, exported as the default of `modules/<mod>/nav.ts`. */
export interface ModuleNav {
  order: number;
  items: NavItem[];
}

interface Ordered {
  order: number;
}

/** Collects the default exports of an `import.meta.glob(..., { eager: true })` result, ordered by `order`. */
export function collectModules<T extends Ordered>(glob: Record<string, { default: T }>): T[] {
  return Object.entries(glob)
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([, mod]) => mod.default)
    .sort((a, b) => a.order - b.order);
}
