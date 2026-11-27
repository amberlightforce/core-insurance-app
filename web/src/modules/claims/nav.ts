import { claimsRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 70,
  items: [
    { id: 'claims', to: '/claims', roles: claimsRoles },
    {
      id: 'claims',
      to: '/claims/fs-statements',
      label: 'fsStatements',
      icon: 'fsStatements',
      roles: claimsRoles,
    },
  ],
} satisfies ModuleNav;
