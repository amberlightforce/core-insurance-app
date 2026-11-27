import { reinsuranceRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 90,
  items: [{ id: 'reinsurance', to: '/reinsurance', roles: reinsuranceRoles }],
} satisfies ModuleNav;
