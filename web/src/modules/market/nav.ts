import { packRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 150,
  items: [{ id: 'admin', to: '/admin/packs', label: 'packs', icon: 'packs', roles: packRoles }],
} satisfies ModuleNav;
