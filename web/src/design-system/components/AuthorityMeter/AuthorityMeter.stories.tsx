import type { Meta, StoryObj } from '@storybook/react-vite';

import { AuthorityMeter } from './AuthorityMeter';

const meta = {
  title: 'Components/AuthorityMeter',
  component: AuthorityMeter,
  args: { label: 'Όριο εξουσιοδότησης', value: '6480.00', limit: '60000.00' },
  decorators: [
    (Story) => (
      <div style={{ maxInlineSize: '360px' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof AuthorityMeter>;

export default meta;
type Story = StoryObj<typeof meta>;

export const WithinLimit: Story = {};

export const Warning: Story = { args: { value: '52000.00' } };

export const OverLimit: Story = { args: { value: '64800.00' } };

/** The 6 px decision bar of the approval workbench. */
export const DecisionBar: Story = { args: { size: 'decision', value: '41250.00' } };

export const PercentDeviation: Story = {
  args: { label: 'Όριο σας ±10 %', value: -7.5, limit: 10, format: 'percent' },
};
