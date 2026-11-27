import { productRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 140,
  items: [{ id: 'products', to: '/products', roles: productRoles }],
} satisfies ModuleNav;
