import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import { Button } from '../Button';
import { Skeleton, SkeletonRegion, SkeletonText } from './Skeleton';

const meta = {
  title: 'Components/Skeleton',
  component: Skeleton,
  args: { shape: 'line', width: '72%' },
  argTypes: { shape: { control: 'inline-radio', options: ['line', 'block', 'circle'] } },
} satisfies Meta<typeof Skeleton>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const RecordCard: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-3)', maxWidth: 360 }}>
      <div style={{ display: 'flex', gap: 'var(--space-3)', alignItems: 'center' }}>
        <Skeleton shape="circle" width="32px" />
        <div style={{ display: 'grid', gap: 'var(--space-1)', flex: 1 }}>
          <Skeleton width="60%" />
          <Skeleton width="40%" />
        </div>
      </div>
      <Skeleton shape="block" height="96px" />
      <SkeletonText lines={4} />
    </div>
  ),
};

function DelayedDemo() {
  const [loading, setLoading] = useState(false);
  return (
    <div style={{ display: 'grid', gap: 'var(--space-3)', maxWidth: 360 }}>
      <Button
        onPress={() => {
          setLoading(true);
          setTimeout(() => {
            setLoading(false);
          }, 1200);
        }}
      >
        Φόρτωση πληρωμών
      </Button>
      <SkeletonRegion isLoading={loading} fallback={<SkeletonText lines={3} />}>
        <p>Πληρωμή 500,00 € · εγκρίθηκε από Μ. Παπαδοπούλου</p>
      </SkeletonRegion>
    </div>
  );
}

export const DelayedRegion: Story = { render: () => <DelayedDemo /> };
