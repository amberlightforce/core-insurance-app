import { complianceRoles, dsarRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 120,
  items: [
    { id: 'compliance', to: '/compliance', roles: complianceRoles },
    {
      id: 'compliance',
      to: '/compliance/complaints',
      label: 'complaints',
      icon: 'complaints',
      roles: complianceRoles,
    },
    {
      id: 'compliance',
      to: '/compliance/dsar',
      label: 'dsar',
      icon: 'dsar',
      roles: dsarRoles,
    },
  ],
} satisfies ModuleNav;
