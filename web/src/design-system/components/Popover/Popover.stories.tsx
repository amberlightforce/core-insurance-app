import type { Meta, StoryObj } from '@storybook/react-vite';
import { Filter } from 'lucide-react';

import { Button } from '../Button';
import { ExplainWhy } from './ExplainWhy';
import { HoverCard } from './HoverCard';
import { Popover } from './Popover';

const meta = {
  title: 'Components/Popover',
  component: Popover,
  args: {
    trigger: <Button icon={Filter}>Φίλτρα</Button>,
    title: 'Φίλτρο κατάστασης',
    size: 'md',
    status: 'ready',
    children: <p>Επιλέξτε τις καταστάσεις που θέλετε να δείτε.</p>,
  },
  argTypes: {
    size: { control: 'inline-radio', options: ['sm', 'md', 'lg', 'filters'] },
    status: { control: 'inline-radio', options: ['ready', 'loading', 'error'] },
  },
} satisfies Meta<typeof Popover>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const WithFooter: Story = {
  args: {
    size: 'filters',
    footer: (
      <>
        <Button variant="ghost">Εκκαθάριση</Button>
        <Button variant="primary">Εφαρμογή</Button>
      </>
    ),
  },
};

export const Loading: Story = { args: { status: 'loading', defaultOpen: true } };

export const ErrorState: Story = {
  args: { status: 'error', defaultOpen: true, onRetry: () => undefined },
};

export const QuickLook: Story = {
  render: () => (
    <p>
      Ασφαλιστήριο{' '}
      <HoverCard
        label={<span className="ds-mono">ΑΣΦ-2026-004471</span>}
        href="#"
        title="Ασφαλιστήριο ΑΣΦ-2026-004471"
      >
        <strong>Μαρία Παπαδοπούλου</strong>
        <p className="ds-caption">Αυτοκίνητο ΙΧ · Σε ισχύ · Λήξη 14/03/2027</p>
        <div style={{ display: 'flex', gap: 'var(--space-2)' }}>
          <Button size="sm" variant="primary">
            Άνοιγμα
          </Button>
          <Button size="sm">Άνοιγμα σε νέα καρτέλα</Button>
        </div>
      </HoverCard>{' '}
      — περάστε τον δείκτη ή πατήστε Space.
    </p>
  ),
};

export const Explain: Story = {
  render: () => (
    <p>
      Απόθεμα 6.480,00 €{' '}
      <ExplainWhy
        value="6.480,00 €"
        basis="Μοντέλο CLM-RES v4.2 · βεβαιότητα 0,91"
        factors={[
          { label: 'Κάλυψη υλικών ζημιών', value: 4200, display: '+4.200,00 €' },
          { label: 'Ιστορικό ζημιών', value: 2600, display: '+2.600,00 €' },
          { label: 'Απαλλαγή', value: -320, display: '−320,00 €' },
        ]}
        sources={[
          { label: 'Έκθεση πραγματογνώμονα, σελ. 3', href: '#' },
          { label: 'Τιμολόγιο συνεργείου 1182', href: '#' },
        ]}
        onViewFullSheet={() => undefined}
      />
    </p>
  ),
};
