import type { Meta, StoryObj } from '@storybook/react-vite';
import { Mail } from 'lucide-react';
import { useState } from 'react';

import { SearchField } from './SearchField';
import { TextField } from './TextField';

const meta = {
  title: 'Components/TextField',
  component: TextField,
  args: { label: 'Ονοματεπώνυμο', size: 'md' },
  argTypes: {
    type: { control: 'inline-radio', options: ['text', 'email', 'tel', 'url'] },
    size: { control: 'inline-radio', options: ['sm', 'md', 'lg'] },
  },
} satisfies Meta<typeof TextField>;

export default meta;
type Story = StoryObj<typeof meta>;

const column = {
  display: 'grid',
  gap: 'var(--space-stack-field)',
  maxInlineSize: '420px',
} as const;

export const Playground: Story = {};

export const Variants: Story = {
  render: () => (
    <div style={column}>
      <TextField label="Ονοματεπώνυμο" isRequired autoComplete="name" />
      <TextField
        label="Email"
        type="email"
        autoComplete="email"
        leadingIcon={Mail}
        helperText="Θα στείλουμε εδώ την προσφορά."
      />
      <TextField label="Κινητό τηλέφωνο" type="tel" helperText="Ομαδοποιείται ως 69x xxx xxxx" />
      <TextField label="Ιστότοπος" type="url" placeholder="https://" />
      <TextField label="Ποσό απαίτησης" prefix="€" />
      <TextField label="Κυβικά" suffix="cc" />
      <TextField
        label="Περιγραφή συμβάντος"
        multiline
        maxLength={500}
        helperText="Ctrl+Enter για υποβολή"
        help="Περιγράψτε πού, πότε και πώς έγινε το συμβάν."
      />
    </div>
  ),
};

export const States: Story = {
  render: () => (
    <div style={column}>
      <TextField label="Προεπιλογή" defaultValue="Μαρία Παπαδοπούλου" />
      <TextField
        label="Ημερομηνία ζημίας"
        defaultValue="32/10/2026"
        errorMessage="Η ημερομηνία δεν υπάρχει. Συμπληρώστε τη με τη μορφή ηη/μμ/εεεε."
      />
      <TextField
        label="Email"
        defaultValue="maria@exmaple.gr"
        warningMessage="Μήπως εννοείτε example.gr;"
      />
      <TextField
        label="ΑΦΜ"
        defaultValue="090000045"
        disabledReason="Ο ΑΦΜ κλειδώθηκε μετά τον έλεγχο στο Μητρώο."
      />
      <TextField label="Χωρίς αιτία" isDisabled defaultValue="—" />
      <TextField label="ΔΟΥ" isLoading defaultValue="Α' Αθηνών" />
      <TextField label="Αριθμός συμβολαίου" isReadOnly defaultValue="ΑΣΦ-2026-0412" mono />
      <TextField
        label="IBAN"
        isReadOnly
        isMasked
        defaultValue="•••• 4471"
        onReveal={() => undefined}
      />
      <TextField
        label="ΔΟΥ"
        defaultValue="Α' Αθηνών"
        isAiSuggested
        aiSource="Από gov.gr Wallet · βεβαιότητα 94%"
      />
    </div>
  ),
};

export const Sizes: Story = {
  render: () => (
    <div style={column}>
      <TextField label="Μικρό" size="sm" />
      <TextField label="Μεσαίο" size="md" />
      <TextField label="Μεγάλο" size="lg" />
    </div>
  ),
};

function MaskedRevealDemo() {
  const [revealed, setRevealed] = useState(false);
  return (
    <TextField
      label="IBAN"
      isReadOnly
      mono
      isMasked={!revealed}
      value={revealed ? 'GR16 0110 1250 0000 0001 2300 695' : '•••• 0695'}
      onReveal={() => {
        setRevealed(true);
      }}
    />
  );
}

export const MaskedReveal: Story = { render: () => <MaskedRevealDemo /> };

export const Search: Story = {
  render: () => (
    <div style={column}>
      <SearchField label="Αναζήτηση πελάτη" shortcut="Mod+K" />
      <SearchField aria-label="Αναζήτηση στη λίστα" defaultValue="Παπαδ" size="sm" />
    </div>
  ),
};
