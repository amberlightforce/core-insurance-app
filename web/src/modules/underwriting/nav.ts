import { underwritingRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 50,
  items: [
    { id: 'underwriting', to: '/underwriting', roles: underwritingRoles },
    {
      id: 'policies',
      to: '/policies/pack-rollback',
      label: 'packRollback',
      icon: 'packRollback',
      roles: underwritingRoles,
    },
  ],
} satisfies ModuleNav;
