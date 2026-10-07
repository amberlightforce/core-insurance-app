import type { Meta, StoryObj } from '@storybook/react-vite';
import { Download, Search, UserCheck } from 'lucide-react';

import { Button } from './Button';

const meta = {
  title: 'Components/Button',
  component: Button,
  args: { children: 'Αποθήκευση', variant: 'secondary', size: 'md' },
  argTypes: {
    variant: {
      control: 'select',
      options: ['primary', 'commit', 'secondary', 'ghost', 'danger', 'danger-ghost', 'link', 'ai'],
    },
    size: { control: 'inline-radio', options: ['sm', 'md', 'lg'] },
  },
} satisfies Meta<typeof Button>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const Variants: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-3)', flexWrap: 'wrap', alignItems: 'center' }}>
      <Button variant="commit">Έκδοση ασφαλιστηρίου</Button>
      <Button variant="primary">Υπολογισμός</Button>
      <Button variant="secondary">Αποθήκευση πρόχειρου</Button>
      <Button variant="ghost" icon={Download}>
        Λήψη
      </Button>
      <Button variant="danger-ghost">Ακύρωση ασφαλιστηρίου…</Button>
      <Button variant="danger">Ακύρωση ασφαλιστηρίου</Button>
      <Button variant="link">Γιατί;</Button>
      <Button variant="ai">Αποδοχή</Button>
      <Button variant="primary" icon={UserCheck}>
        Αποστολή για έγκριση
      </Button>
    </div>
  ),
};

export const Sizes: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-3)', alignItems: 'center' }}>
      <Button size="sm">Μικρό</Button>
      <Button size="md">Μεσαίο</Button>
      <Button size="lg" variant="commit" shortcut="Mod+Enter">
        Έγκριση
      </Button>
      <Button size="md" icon={Search} label="Αναζήτηση" variant="ghost" shortcut="Mod+K" />
    </div>
  ),
};

export const States: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-3)', alignItems: 'center' }}>
      <Button
        variant="commit"
        disabledReason="Δεν μπορεί να γίνει δέσμευση: 2 ανοιχτά ζητήματα ανάληψης"
      >
        Δέσμευση
      </Button>
      <Button variant="primary" isLoading>
        Υποβολή
      </Button>
      <Button isDisabled>Χωρίς αιτία</Button>
      <Button isPressed>Επιλεγμένο</Button>
    </div>
  ),
};
