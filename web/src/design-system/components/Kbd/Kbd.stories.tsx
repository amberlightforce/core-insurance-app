import type { Meta, StoryObj } from '@storybook/react-vite';

import { Kbd } from './Kbd';

const meta = {
  title: 'Components/Kbd',
  component: Kbd,
  args: { shortcut: 'Mod+K' },
} satisfies Meta<typeof Kbd>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};

export const Tones: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-4)', alignItems: 'center' }}>
      <Kbd shortcut="Mod+Enter" />
      <Kbd shortcut="Shift+F10" />
      <span
        style={{
          background: 'var(--color-surface-inverse)',
          color: 'var(--color-text-inverse)',
          padding: 'var(--space-2)',
          borderRadius: 'var(--radius-sm)',
        }}
      >
        <Kbd shortcut="Mod+C" tone="inverse" />
      </span>
      <Kbd shortcut="Enter" tone="subtle" />
    </div>
  ),
};
