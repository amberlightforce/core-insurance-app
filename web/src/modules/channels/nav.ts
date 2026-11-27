import { withdrawalRoles } from '../../auth/roles';
import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 60,
  items: [
    {
      id: 'policies',
      to: '/channels/withdrawals',
      label: 'withdrawals',
      icon: 'withdrawals',
      roles: withdrawalRoles,
    },
  ],
} satisfies ModuleNav;
