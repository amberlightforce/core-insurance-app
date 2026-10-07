import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import { Stepper } from './Stepper';
import type { StepItem } from './stepperLogic';

const motorQuote: StepItem[] = [
  { id: 'policyholder', label: 'Λήπτης', state: 'complete', subLabel: 'Παπαδόπουλος Γεώργιος' },
  { id: 'vehicles', label: 'Οχήματα', state: 'complete', subLabel: '3 οχήματα · 2 οδηγοί' },
  { id: 'coverages', label: 'Καλύψεις', state: 'error', errorCount: 2, subLabel: '2 σφάλματα' },
  { id: 'underwriting', label: 'Ανάληψη', state: 'warning', subLabel: '1 ζήτημα ανάληψης' },
  { id: 'pricing', label: 'Τιμολόγηση', state: 'loading' },
  {
    id: 'documents',
    label: 'Έγγραφα',
    state: 'locked',
    lockedReason: 'Απαιτείται έγκριση ανάληψης',
  },
  { id: 'review', label: 'Έλεγχος και δέσμευση', state: 'upcoming' },
];

const meta = {
  title: 'Components/Stepper',
  component: Stepper,
  args: { steps: motorQuote, currentId: 'coverages', orientation: 'vertical' },
  argTypes: { orientation: { control: 'inline-radio', options: ['vertical', 'horizontal'] } },
} satisfies Meta<typeof Stepper>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Click a step or use Alt+→ / Alt+←. */
export const Vertical: Story = {
  render: function Render(args) {
    const [current, setCurrent] = useState(args.currentId);
    return <Stepper {...args} currentId={current} onNavigate={setCurrent} />;
  },
};

export const Horizontal: Story = {
  args: { orientation: 'horizontal' },
  render: function Render(args) {
    const [current, setCurrent] = useState(args.currentId);
    return (
      <div style={{ maxInlineSize: 720 }}>
        <Stepper {...args} currentId={current} onNavigate={setCurrent} />
      </div>
    );
  },
};

/** A bound job: every step complete and navigable. */
export const ReadOnly: Story = {
  args: {
    steps: motorQuote.map((step) => ({ ...step, state: 'complete' as const })),
    currentId: 'review',
  },
};
