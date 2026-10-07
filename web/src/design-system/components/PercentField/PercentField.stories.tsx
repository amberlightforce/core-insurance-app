import type { Meta, StoryObj } from '@storybook/react-vite';

import { PercentField } from './PercentField';

const meta = {
  title: 'Components/PercentField',
  component: PercentField,
  args: { label: 'Συντελεστής' },
  decorators: [
    (Story) => (
      <div style={{ maxInlineSize: '280px' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof PercentField>;

export default meta;
type Story = StoryObj<typeof meta>;

/** ↑/↓ step by 1, Shift by 10; typed decimals are kept. */
export const Default: Story = { args: { defaultValue: 15 } };

export const Rating: Story = {
  args: { label: 'Συντελεστής ασφαλίστρου', fractionDigits: 4, defaultValue: 2.4375 },
};

export const PerMille: Story = {
  args: { label: 'Συντελεστής σεισμού', perMille: true, defaultValue: 2.5 },
};

/** Deviation against the technical premium (416,00 €) with the user's ±10 % authority. */
export const Deviation: Story = {
  args: {
    label: 'Απόκλιση από τεχνικό ασφάλιστρο',
    variant: 'deviation',
    defaultValue: -7.5,
    baseAmount: '416.00',
    authorityLimit: 10,
  },
};

export const States: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-6)' }}>
      <PercentField label="Έκπτωση" isRequired />
      <PercentField
        label="Έκπτωση"
        defaultValue={45}
        errorMessage="Η έκπτωση υπερβαίνει το 30 %. Διορθώστε την έκπτωση."
      />
      <PercentField
        label="Έκπτωση"
        defaultValue={5}
        disabledReason="Κλειδωμένο από την ανάληψη κινδύνου"
      />
      <PercentField label="Έκπτωση" defaultValue={5} isDisabled />
      <PercentField label="Έκπτωση" defaultValue={5} isReadOnly />
    </div>
  ),
};
