import type { Meta, StoryObj } from '@storybook/react-vite';

import { searchItems } from '../../../format/search';
import { Combobox } from './Combobox';
import type { ComboboxOption } from './options';

const people: ComboboxOption[] = [
  {
    id: 'p1',
    label: 'Γεώργιος Παπαδόπουλος',
    caption: '1978 · ΑΦΜ •••789 · Αθήνα',
    meta: 'ΠΡΣ-004471',
  },
  {
    id: 'p2',
    label: 'Γεώργιος Παπαδόπουλος',
    caption: '1991 · ΑΦΜ •••204 · Λάρισα',
    meta: 'ΠΡΣ-009812',
  },
  {
    id: 'p3',
    label: 'Γεώργιος Παπαδάκης',
    caption: '1985 · ΑΦΜ •••112 · Ηράκλειο',
    meta: 'ΠΡΣ-001203',
  },
  { id: 'p4', label: 'Μαρία Αποστόλου', caption: '1990 · ΑΦΜ •••541 · Πάτρα', meta: 'ΠΡΣ-002210' },
  {
    id: 'p5',
    label: 'Ευάγγελος Θεοδωρίδης',
    caption: '1969 · ΑΦΜ •••300 · Θεσσαλονίκη',
    meta: 'ΠΡΣ-003377',
  },
  { id: 'p6', label: 'Ζωή Χατζή', caption: '2001 · ΑΦΜ •••918 · Χανιά', meta: 'ΠΡΣ-005160' },
];

const grouped: ComboboxOption[] = [
  { id: 'g1', label: 'Γεώργιος Παπαδόπουλος', caption: 'Πελάτης · Αθήνα', group: 'Πρόσωπα' },
  {
    id: 'g2',
    label: 'Παπαδόπουλος Ασφαλιστική Πρακτορεία',
    caption: 'Συνεργάτης',
    group: 'Πρόσωπα',
  },
  {
    id: 'g3',
    label: 'ΑΣΦ-2026-004471',
    caption: 'Παπαδόπουλος · Αυτοκίνητο · σε ισχύ',
    meta: '412,38 €',
    group: 'Ασφαλιστήρια',
  },
  {
    id: 'g4',
    label: 'ΖΗΜ-2026-000318',
    caption: 'Παπαδόπουλος · Θραύση κρυστάλλων',
    group: 'Ζημίες',
  },
];

function fakeServer(failEvery = 0) {
  let calls = 0;
  return (query: string, signal: AbortSignal) =>
    new Promise<readonly ComboboxOption[]>((resolve, reject) => {
      calls += 1;
      const id = setTimeout(() => {
        if (failEvery > 0 && calls % failEvery === 1) reject(new Error('503'));
        else
          resolve(
            searchItems(people, query, (p) => [p.label, p.caption ?? '', p.meta ?? '']).map(
              (h) => h.item,
            ),
          );
      }, 600);
      signal.addEventListener('abort', () => {
        clearTimeout(id);
      });
    });
}

const meta = {
  title: 'Components/Combobox',
  component: Combobox,
  args: { label: 'Πελάτης', items: people, placeholder: 'Όνομα, ΑΦΜ ή κωδικός' },
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div style={{ maxInlineSize: '420px' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof Combobox>;

export default meta;
type Story = StoryObj<typeof meta>;

/** Type «papad», «Παπαδ», «θεσσαλονικη» or «4471». */
export const Single: Story = { args: { description: 'Αναζήτηση με όνομα, ΑΦΜ ή κωδικό προσώπου' } };

export const Grouped: Story = {
  args: { label: 'Αναζήτηση', items: grouped, showSearchIcon: true },
};

export const Async: Story = {
  render: () => (
    <Combobox
      label="Πελάτης"
      loadOptions={fakeServer()}
      description="Τουλάχιστον 2 χαρακτήρες (1 για κωδικό)"
    />
  ),
};

/** The first request fails: Retry keeps the query. */
export const AsyncError: Story = {
  render: () => <Combobox label="Πελάτης" loadOptions={fakeServer(2)} />,
};

export const Creatable: Story = {
  args: {
    onCreate: () => undefined,
    createLabel: 'Δημιουργία νέου προσώπου…',
  },
};

export const States: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-6)' }}>
      <Combobox label="Πελάτης" items={people} isRequired />
      <Combobox
        label="Πελάτης"
        items={people}
        errorMessage="Επιλέξτε πελάτη από τη λίστα. Αν δεν υπάρχει, δημιουργήστε νέο πρόσωπο."
      />
      <Combobox
        label="Πελάτης"
        items={people}
        disabledReason="Ο πελάτης κλειδώθηκε στην προσφορά"
      />
      <Combobox label="Πελάτης" items={people} isDisabled />
      <Combobox
        label="Πελάτης"
        items={people}
        isReadOnly
        defaultSelectedKey="p1"
        defaultInputValue="Γεώργιος Παπαδόπουλος"
      />
    </div>
  ),
};
