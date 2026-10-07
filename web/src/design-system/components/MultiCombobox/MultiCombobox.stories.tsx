import type { Meta, StoryObj } from '@storybook/react-vite';

import type { ComboboxOption } from '../Combobox';
import { MultiCombobox } from './MultiCombobox';

const covers: ComboboxOption[] = [
  { id: 'fire', label: 'Πυρκαγιά', caption: 'Βασική κάλυψη', group: 'Βασικές' },
  { id: 'lightning', label: 'Κεραυνός', caption: 'Βασική κάλυψη', group: 'Βασικές' },
  { id: 'theft', label: 'Κλοπή', caption: 'Προαιρετική', group: 'Προαιρετικές' },
  { id: 'quake', label: 'Σεισμός', caption: 'Προαιρετική · απαλλαγή 2 %', group: 'Προαιρετικές' },
  { id: 'flood', label: 'Πλημμύρα', caption: 'Προαιρετική', group: 'Προαιρετικές' },
  { id: 'glass', label: 'Θραύση κρυστάλλων', caption: 'Προαιρετική', group: 'Προαιρετικές' },
  { id: 'liability', label: 'Αστική ευθύνη', caption: 'Προαιρετική', group: 'Προαιρετικές' },
  {
    id: 'riot',
    label: 'Στάσεις, απεργίες, πολιτικές ταραχές',
    caption: 'Προαιρετική',
    group: 'Προαιρετικές',
  },
];

const meta = {
  title: 'Components/MultiCombobox',
  component: MultiCombobox,
  args: { label: 'Καλύψεις', items: covers, placeholder: 'Αναζήτηση κάλυψης' },
  decorators: [
    (Story) => (
      <div style={{ maxInlineSize: '420px' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof MultiCombobox>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Type «seism», «klopi» or «πλημ»; Backspace removes the last chip, ← walks the chips. */
export const Default: Story = { args: { defaultSelectedKeys: ['fire', 'quake'] } };

/** Many selections wrap to three lines, then collapse into «+N». */
export const Overflow: Story = {
  args: { defaultSelectedKeys: covers.map((c) => c.id) },
  decorators: [
    (Story) => (
      <div style={{ maxInlineSize: '260px' }}>
        <Story />
      </div>
    ),
  ],
};

export const States: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-6)' }}>
      <MultiCombobox
        label="Καλύψεις"
        items={covers}
        isRequired
        errorMessage="Επιλέξτε τουλάχιστον μία κάλυψη."
      />
      <MultiCombobox
        label="Καλύψεις"
        items={covers}
        defaultSelectedKeys={['fire']}
        disabledReason="Το προϊόν δεν επιτρέπει αλλαγές καλύψεων"
      />
      <MultiCombobox
        label="Καλύψεις"
        items={covers}
        defaultSelectedKeys={['fire', 'theft']}
        isReadOnly
      />
    </div>
  ),
};
