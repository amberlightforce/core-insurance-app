import type { Meta, StoryObj } from '@storybook/react-vite';
import { Copy } from 'lucide-react';
import { Button as AriaButton } from 'react-aria-components';

import { Button } from '../Button';
import { Kbd } from '../Kbd';
import { Spinner } from '../Spinner';
import { Tooltip } from './Tooltip';

const meta = {
  title: 'Components/Tooltip',
  component: Tooltip,
  args: {
    content: 'Αντιγραφή αριθμού ασφαλιστηρίου',
    children: <AriaButton>Εστίαση ή αιώρηση</AriaButton>,
  },
} satisfies Meta<typeof Tooltip>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};

export const Open: Story = { args: { isOpen: true, shortcut: 'Mod+C' } };

export const OnIconButton: Story = {
  render: () => <Button variant="ghost" icon={Copy} label="Αντιγραφή" shortcut="Mod+C" />,
};

export const KbdAndSpinner: Story = {
  name: 'Kbd chip and spinner',
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-4)', alignItems: 'center' }}>
      <Kbd shortcut="Mod+K" />
      <Kbd shortcut="Mod+Enter" />
      <Kbd shortcut="Shift+F10" />
      <Spinner delayed={false} size={12} />
      <Spinner delayed={false} size={16} />
      <Spinner delayed={false} size={24} />
    </div>
  ),
};
