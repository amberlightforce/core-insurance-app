import type { Meta, StoryObj } from '@storybook/react-vite';

import { IdentifierField } from './IdentifierField';

const meta = {
  title: 'Components/IdentifierField',
  component: IdentifierField,
  args: { kind: 'afm', label: 'ΑΦΜ' },
  argTypes: {
    kind: { control: 'inline-radio', options: ['afm', 'iban', 'plate', 'vin', 'reference'] },
  },
} satisfies Meta<typeof IdentifierField>;

export default meta;
type Story = StoryObj<typeof meta>;

const column = {
  display: 'grid',
  gap: 'var(--space-stack-field)',
  maxInlineSize: '420px',
} as const;

export const Playground: Story = {};

export const Kinds: Story = {
  render: () => (
    <div style={column}>
      <IdentifierField kind="afm" label="ΑΦΜ" defaultValue="090000045" isRequired />
      <IdentifierField
        kind="iban"
        label="IBAN δικαιούχου"
        defaultValue="GR1601101250000000012300695"
        helperText="Τράπεζα: Εθνική Τράπεζα της Ελλάδος"
      />
      <IdentifierField
        kind="plate"
        label="Αριθμός κυκλοφορίας"
        helperText="Πληκτρολογήστε και με λατινικά: ikx1234 → ΙΚΧ-1234"
      />
      <IdentifierField kind="vin" label="Αριθμός πλαισίου (VIN)" defaultValue="WVWZZZ1JZXW000001" />
      <IdentifierField
        kind="reference"
        label="Αριθμός ζημίας"
        referencePattern={/^ΖΗΜ-\d{4}-\d{6}$/u}
        defaultValue="ΖΗΜ-2026-004471"
      />
    </div>
  ),
};

export const States: Story = {
  render: () => (
    <div style={column}>
      <IdentifierField
        kind="afm"
        label="ΑΦΜ"
        defaultValue="090000046"
        errorMessage="Ο ΑΦΜ δεν είναι έγκυρος: το τελευταίο ψηφίο ελέγχου δεν ταιριάζει. Ελέγξτε τον αριθμό."
      />
      <IdentifierField
        kind="afm"
        label="ΑΦΜ (έλεγχος στο Μητρώο)"
        defaultValue="090000045"
        isLoading
      />
      <IdentifierField
        kind="iban"
        label="IBAN"
        defaultValue="GR1601101250000000012300695"
        isReadOnly
      />
      <IdentifierField
        kind="plate"
        label="Πινακίδα"
        defaultValue="ΙΚΧ1234"
        disabledReason="Η πινακίδα δεν αλλάζει μετά την έκδοση."
      />
    </div>
  ),
};
