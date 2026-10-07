import type { Meta, StoryObj } from '@storybook/react-vite';
import { Copy } from 'lucide-react';

import { Button } from '../Button';
import { Card, WorkLeftCard } from './index';

const meta = {
  title: 'Components/Card',
  component: Card,
  args: {
    title: 'Στοιχεία οχήματος',
    meta: 'Ενημέρωση 14:32',
    children: 'ΙΧ επιβατικό · ΙΚΑ 1234 · 2019',
  },
  argTypes: {
    variant: { control: 'select', options: ['static', 'interactive', 'selectable', 'work-left'] },
    state: { control: 'inline-radio', options: ['ready', 'loading', 'error', 'empty'] },
  },
} satisfies Meta<typeof Card>;

export default meta;
type Story = StoryObj<typeof meta>;

const grid = {
  display: 'grid',
  gap: 'var(--space-4)',
  gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))',
} as const;

export const Playground: Story = {};

export const Variants: Story = {
  render: () => (
    <div style={grid}>
      <Card title="Στατική κάρτα" meta="Σε φύλλο" footer={<span>Υποσέλιδο</span>}>
        Περιεχόμενο κάρτας με κείμενο σώματος.
      </Card>
      <Card title="Σε καμβά" onCanvas>
        Raised επιφάνεια με elevation.1.
      </Card>
      <Card
        variant="interactive"
        title="ΑΣΦ-2026-0412"
        meta="Ανανέωση σε 23 ημέρες"
        href="#policy"
        actions={<Button variant="ghost" size="sm" icon={Copy} label="Αντιγραφή αριθμού" />}
      >
        Παπαδόπουλος Γεώργιος · Αυτοκίνητο ΙΧ
      </Card>
      <Card variant="selectable" title="Πακέτο Βασικό" isSelected>
        ΑΜ, Νομική προστασία, Οδική βοήθεια
      </Card>
    </div>
  ),
};

export const WorkLeft: Story = {
  render: () => (
    <div style={grid}>
      <WorkLeftCard
        title="Παραπομπές"
        family="warning"
        count={12}
        items={[
          { id: '1', label: 'Παπαδόπουλος Γ. · Αυτοκίνητο' },
          { id: '2', label: 'Νικολάου Κ. · Κατοικία' },
          { id: '3', label: 'Γεωργίου Μ. · Αυτοκίνητο' },
        ]}
        continueHref="#referrals"
      />
      <WorkLeftCard
        title="Ληξιπρόθεσμες εργασίες"
        family="danger"
        count={3}
        continueHref="#tasks"
      />
    </div>
  ),
};

export const States: Story = {
  render: () => (
    <div style={grid}>
      <Card title="Φόρτωση" state="loading" />
      <Card
        title="Σφάλμα"
        state="error"
        errorMessage="Δεν ήταν δυνατή η φόρτωση των πληρωμών."
        correlationId="4f2a91c0"
        onRetry={() => undefined}
      />
      <Card title="Κενή" state="empty" emptyAction={<Button size="sm">Νέα πληρωμή</Button>} />
    </div>
  ),
};
