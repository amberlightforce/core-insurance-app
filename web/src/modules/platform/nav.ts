import { aiAdminRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 170,
  items: [
    {
      id: 'admin',
      to: '/admin/ai',
      label: 'aiControl',
      icon: 'aiControl',
      roles: aiAdminRoles,
    },
  ],
} satisfies ModuleNav;
