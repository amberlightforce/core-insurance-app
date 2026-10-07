import type { Meta, StoryObj } from '@storybook/react-vite';

import { amountInWords } from '../../../format/amount-in-words';
import { CurrencyField } from './CurrencyField';

const meta = {
  title: 'Components/CurrencyField',
  component: CurrencyField,
  args: { label: 'Ποσό αποζημίωσης' },
  decorators: [
    (Story) => (
      <div style={{ maxInlineSize: '280px' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof CurrencyField>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Type «1234567», «12k», «1,5ε»; paste «1234.56» for the interpretation hint; ↑/↓ do nothing. */
export const Default: Story = { args: { defaultValue: '1234.56', description: 'Με ΦΠΑ' } };

export const AuthorityLimit: Story = {
  args: { defaultValue: '6480.00', authorityLimit: '5000.00' },
};

export const AmountInWords: Story = {
  args: {
    label: 'Ποσό πληρωμής',
    defaultValue: '12480.00',
    showAmountInWords: true,
    toWords: (value: string) => amountInWords(value),
    announceWords: true,
  },
};

export const NegativeAdverse: Story = {
  args: { label: 'Διαφορά', defaultValue: '-412.38', allowNegative: true, adverse: true },
};

export const RegionEnGb: Story = { args: { defaultValue: '1234.56', region: 'en-GB' } };

export const States: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-6)' }}>
      <CurrencyField label="Ποσό" isRequired />
      <CurrencyField label="Ποσό" errorMessage="Συμπληρώστε το ποσό αποζημίωσης." />
      <CurrencyField
        label="Ποσό"
        defaultValue="250.00"
        disabledReason="Το ποσό ορίζεται από το τιμολόγιο"
      />
      <CurrencyField label="Ποσό" defaultValue="250.00" isDisabled />
      <CurrencyField label="Ποσό" defaultValue="999999999.99" isReadOnly />
    </div>
  ),
};
