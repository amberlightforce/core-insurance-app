import { migrationRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 160,
  items: [
    {
      id: 'admin',
      to: '/admin/migration',
      label: 'migration',
      icon: 'migration',
      roles: migrationRoles,
    },
  ],
} satisfies ModuleNav;
