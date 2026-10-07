import type { Meta, StoryObj } from '@storybook/react-vite';

import { Select, type SelectOption, type SelectSection } from './Select';

const usage: SelectOption[] = [
  { id: 'private', label: 'Ιδιωτική χρήση', description: 'Μετακινήσεις και αναψυχή' },
  { id: 'professional', label: 'Επαγγελματική χρήση', description: 'Πωλητές, τεχνικοί' },
  { id: 'rental', label: 'Ενοικίαση' },
  { id: 'taxi', label: 'Ταξί', isDisabled: true },
];

const coverages: SelectSection[] = [
  {
    id: 'mandatory',
    title: 'Υποχρεωτικές',
    options: [{ id: 'tpl', code: '01', label: 'Αστική ευθύνη' }],
  },
  {
    id: 'optional',
    title: 'Προαιρετικές',
    options: [
      { id: 'legal', code: '02', label: 'Νομική προστασία' },
      { id: 'road', code: '03', label: 'Οδική βοήθεια' },
      { id: 'glass', code: '04', label: 'Θραύση κρυστάλλων' },
    ],
  },
];

const meta = {
  title: 'Components/Select',
  component: Select,
  args: { label: 'Χρήση οχήματος', options: usage },
  argTypes: { size: { control: 'inline-radio', options: ['sm', 'md', 'lg'] } },
} satisfies Meta<typeof Select>;

export default meta;
type Story = StoryObj<typeof meta>;

const column = {
  display: 'grid',
  gap: 'var(--space-stack-field)',
  maxInlineSize: '360px',
} as const;

export const Playground: Story = {};

export const Sections: Story = {
  args: { label: 'Κάλυψη', sections: coverages },
};

export const States: Story = {
  render: () => (
    <div style={column}>
      <Select label="Προεπιλογή" options={usage} isRequired helperText="Όπως στην άδεια" />
      <Select label="Επιλεγμένο" options={usage} defaultValue="professional" />
      <Select label="Σφάλμα" options={usage} errorMessage="Επιλέξτε τη χρήση του οχήματος." />
      <Select
        label="Προειδοποίηση"
        options={usage}
        defaultValue="rental"
        warningMessage="Η ενοικίαση χρειάζεται έγκριση ανάληψης."
      />
      <Select
        label="Με αιτία"
        options={usage}
        defaultValue="private"
        disabledReason="Η χρήση ορίζεται από το προϊόν."
      />
      <Select label="Χωρίς αιτία" options={usage} isDisabled />
      <Select label="Φόρτωση" options={[]} isLoading />
      <Select label="Μόνο ανάγνωση" options={usage} value="private" isReadOnly />
    </div>
  ),
};

export const Sizes: Story = {
  render: () => (
    <div style={column}>
      <Select label="Μικρό" options={usage} size="sm" />
      <Select label="Μεσαίο" options={usage} size="md" />
      <Select label="Μεγάλο" options={usage} size="lg" />
    </div>
  ),
};
