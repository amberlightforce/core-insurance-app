import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import { Button } from '../Button';
import { Badge } from './Badge';
import { StatusPill } from './StatusPill';
import { allStatusDefinitions, type EntityState } from './statusMap';

const meta = {
  title: 'Components/StatusPill',
  component: StatusPill,
  args: { entity: 'policyTerm', state: 'inForce', variant: 'status', size: 'md' },
  argTypes: {
    variant: {
      control: 'inline-radio',
      options: ['status', 'status-solid', 'status-outline', 'dot'],
    },
    size: { control: 'inline-radio', options: ['sm', 'md'] },
  },
} satisfies Meta<typeof StatusPill>;

export default meta;
type Story = StoryObj<typeof meta>;

const row = {
  display: 'flex',
  gap: 'var(--space-2)',
  flexWrap: 'wrap',
  alignItems: 'center',
} as const;

export const Playground: Story = {};

export const Variants: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-3)' }}>
      <div style={row}>
        <StatusPill entity="job" state="quoted" />
        <StatusPill entity="job" state="bound" />
        <StatusPill entity="policyTerm" state="lapsed" />
        <StatusPill entity="policyTerm" state="cancelled" />
        <StatusPill entity="job" state="referred" />
      </div>
      <div style={row}>
        <StatusPill semantic="breached" />
        <StatusPill semantic="conflict" />
        <StatusPill entity="screeningResult" state="trueMatch" />
      </div>
      <div style={row}>
        <StatusPill semantic="ai-generated" />
        <StatusPill entity="delivery" state="sent" variant="status-outline" />
      </div>
      <div style={row}>
        <StatusPill entity="claim" state="open" variant="dot" />
        <StatusPill entity="claim" state="open" variant="dot" showLabel />
      </div>
      <div style={row}>
        <StatusPill semantic="pending-approval" subLabel="Ομάδα Πληρωμών Β" />
        <StatusPill entity="clockInstance" state="warned" countdownDays={6} />
        <StatusPill entity="outboundDocument" state="rendering" />
        <StatusPill entity="policyTerm" state="inForce" isPending />
        <StatusPill entity="policyTerm" state="inForce" size="sm" />
      </div>
      <div style={row}>
        <Badge count={3} />
        <Badge count={12} tone="attention" label="12 μη αναγνωσμένες ειδοποιήσεις" />
        <Badge count={1240} tone="attention" />
      </div>
    </div>
  ),
};

const lifecycle: EntityState<'policyTerm'>[] = [
  'scheduled',
  'inForce',
  'pendingCancellation',
  'cancelled',
];

/** MI-15 status morph with a polite announcement. */
export const StatusChange: Story = {
  render: function Render() {
    const [index, setIndex] = useState(0);
    const state = lifecycle[index % lifecycle.length] ?? 'inForce';
    return (
      <div style={row}>
        <StatusPill entity="policyTerm" state={state} />
        <Button
          onPress={() => {
            setIndex((i) => i + 1);
          }}
        >
          Επόμενη κατάσταση
        </Button>
      </div>
    );
  },
};

/** Every state in the single status map. */
export const Catalogue: Story = {
  render: () => (
    <div
      style={{
        display: 'grid',
        gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
        gap: 'var(--space-2)',
      }}
    >
      {allStatusDefinitions().map(({ id, ref }) => (
        <div key={id} style={{ display: 'flex', gap: 'var(--space-2)', alignItems: 'center' }}>
          <code className="ds-mono" style={{ minInlineSize: 120 }}>
            {id}
          </code>
          <StatusPill {...ref} announceChanges={false} />
        </div>
      ))}
    </div>
  ),
};
