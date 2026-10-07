import type { Meta, StoryObj } from '@storybook/react-vite';

import { AiCitation, AiSuggestionCard, ApproveTheDiff } from './index';

const body = (
  <p style={{ margin: 0 }}>
    Η ζημία αφορά υλικές ζημιές στο εμπρός αριστερό τμήμα του οχήματος
    <AiCitation n={1} source="Πραγματογνωμοσύνη 03/10/2026" href="#source-1" />. Το κόστος επισκευής
    εκτιμάται σε 6.480,00 €
    <AiCitation n={2} source="Προσφορά συνεργείου" href="#source-2" />.
  </p>
);

const meta = {
  title: 'Patterns/AiSuggestion',
  component: AiSuggestionCard,
  args: {
    state: 'proposed',
    confidence: 0.91,
    model: 'CLM-RES v4.2',
    children: body,
    sourcesCount: 4,
    onAccept: () => undefined,
    onEdit: () => undefined,
    onReject: () => undefined,
    onWhy: () => undefined,
    onSources: () => undefined,
    onStop: () => undefined,
    onUndo: () => undefined,
    onRefresh: () => undefined,
    receipt: { name: 'Κώστα Νικολάου', time: '14:32' },
  },
  argTypes: {
    state: {
      control: 'select',
      options: [
        'generating',
        'proposed',
        'accepted',
        'edited',
        'rejected',
        'expired',
        'low-confidence',
        'error',
        'disabled',
      ],
    },
  },
} satisfies Meta<typeof AiSuggestionCard>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Proposed: Story = {};
export const Generating: Story = { args: { state: 'generating', children: undefined } };
export const Accepted: Story = { args: { state: 'accepted' } };
export const Edited: Story = { args: { state: 'edited' } };
export const Rejected: Story = {
  args: {
    state: 'rejected',
    rejectReasons: [
      { id: 'data', label: 'Λάθος δεδομένα' },
      { id: 'policy', label: 'Αντίκειται στους όρους' },
    ],
    onRejectReason: () => undefined,
  },
};
export const Expired: Story = { args: { state: 'expired' } };
export const LowConfidence: Story = { args: { state: 'low-confidence', confidence: 0.42 } };
export const Unavailable: Story = { args: { state: 'error' } };

const rows = [
  {
    id: 'r1',
    label: 'Απόθεμα · Υλικές ζημιές',
    from: '5.220,00 €',
    to: '6.480,00 €',
    delta: '+1.260,00 €',
  },
  { id: 'r2', label: 'Απόθεμα · Έξοδα', from: '300,00 €', to: '350,00 €', delta: '+50,00 €' },
];

export const Diff: Story = {
  render: () => (
    <ApproveTheDiff
      rows={rows}
      onAccept={() => undefined}
      onEdit={() => undefined}
      onWhy={() => undefined}
    />
  ),
};

export const NeutralDiff: Story = {
  render: () => (
    <ApproveTheDiff
      rows={rows}
      neutral
      label="Αλλαγές προς έγκριση"
      acceptLabel="Έγκριση επιλεγμένων"
      onAccept={() => undefined}
    />
  ),
};
