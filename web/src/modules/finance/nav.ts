import type { ModuleNav } from '../../app-shell/moduleRegistry';

export default {
  order: 100,
  items: [{ id: 'finance', to: '/finance' }],
} satisfies ModuleNav;
