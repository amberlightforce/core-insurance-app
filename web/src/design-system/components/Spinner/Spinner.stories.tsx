import type { Meta, StoryObj } from '@storybook/react-vite';

import { Spinner } from './Spinner';

const meta = {
  title: 'Components/Spinner',
  component: Spinner,
  args: { size: 16, delayed: false },
} satisfies Meta<typeof Spinner>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};

export const Sizes: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-4)', alignItems: 'center' }}>
      {([12, 14, 16, 20, 24] as const).map((size) => (
        <Spinner key={size} size={size} delayed={false} />
      ))}
    </div>
  ),
};

export const Delayed: Story = { name: 'Delayed (appears after 400 ms)', args: { delayed: true } };
