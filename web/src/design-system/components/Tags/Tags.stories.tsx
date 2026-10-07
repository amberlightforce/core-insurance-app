import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import type { FilterChipItem } from './chipText';
import { FilterChips } from './Tags';

const initial: FilterChipItem[] = [
  { id: 'status', field: 'Κατάσταση', values: ['Σε ισχύ', 'Σε εκκρεμή ακύρωση'] },
  { id: 'branch', field: 'Κλάδος', values: ['Αυτοκίνητο', 'Κατοικία', 'Περιουσία επιχειρήσεων'] },
  { id: 'entity', field: 'Νομική οντότητα', values: ['Ελλάδα'], state: 'locked' },
  { id: 'producer', field: 'Παραγωγός', values: ['10233'], state: 'invalid' },
  { id: 'period', field: 'Περίοδος', values: ['2026'], state: 'readOnly' },
];

const meta = {
  title: 'Components/Tags',
  component: FilterChips,
  args: { items: initial },
} satisfies Meta<typeof FilterChips>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Delete/Backspace or × removes; Enter or click reopens the filter (here: logged). */
export const FilterChipsStory: Story = {
  name: 'Filter chips',
  render: function Render() {
    const [items, setItems] = useState(initial);
    const [edited, setEdited] = useState<string | null>(null);
    return (
      <div style={{ display: 'grid', gap: 'var(--space-3)' }}>
        <FilterChips
          items={items}
          onRemove={(id) => {
            setItems((list) => list.filter((item) => item.id !== id));
          }}
          onEdit={setEdited}
        />
        <p className="ds-caption">{edited ? `Άνοιγμα φίλτρου: ${edited}` : ' '}</p>
      </div>
    );
  },
};

export const ReadOnly: Story = {
  args: {
    items: initial.map((item) => ({ ...item, state: 'readOnly' as const })),
  },
};
