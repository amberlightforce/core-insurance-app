import type { Meta, StoryObj } from '@storybook/react-vite';

import { ProgressBar, ProgressRing } from './Progress';

const meta = {
  title: 'Components/Progress',
  component: ProgressBar,
  args: { label: 'Πρόοδος ημέρας', value: 31, maxValue: 50, showCount: true, size: 'md' },
  argTypes: { size: { control: 'inline-radio', options: ['sm', 'md', 'lg'] } },
} satisfies Meta<typeof ProgressBar>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const Linear: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-4)', maxWidth: 360 }}>
      <ProgressBar label="Μεταφόρτωση εγγράφων" value={62} size="sm" />
      <ProgressBar
        label="Πρόοδος ημέρας"
        value={31}
        maxValue={50}
        showCount
        valueText="31 από 50 εργασίες"
      />
      <ProgressBar label="Έκδοση ασφαλιστηρίων" value={11} maxValue={19} size="lg" showCount />
      <ProgressBar label="Ολοκληρώθηκε" value={50} maxValue={50} showCount />
      <ProgressBar aria-label="Φόρτωση αποτελεσμάτων" isIndeterminate />
    </div>
  ),
};

export const Rings: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-4)', alignItems: 'center' }}>
      <ProgressRing aria-label="Βήματα" value={1} maxValue={4} size={16} />
      <ProgressRing aria-label="Βήματα" value={2} maxValue={4} size={24} />
      <ProgressRing aria-label="Βήματα" value={3} maxValue={4} size={40} />
      <ProgressRing aria-label="Βήματα" value={62} size={64} />
    </div>
  ),
};
