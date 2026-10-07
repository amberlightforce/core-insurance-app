import type { Meta, StoryObj } from '@storybook/react-vite';
import { Clock, FileText, ShieldAlert, UserCheck } from 'lucide-react';
import { useState } from 'react';

import { Button } from '../Button';
import { NotificationBell, NotificationCenter, type NotificationItem } from './Notifications';

const initial: NotificationItem[] = [
  {
    id: '1',
    title: 'Παραβίαση προθεσμίας προσφοράς',
    body: 'ΖΗΜ-2026-000091 · η προθεσμία 3 μηνών έληξε στις 07/10/2026',
    time: 'πριν 5 λεπτά',
    unread: true,
    family: 'danger',
    icon: Clock,
    actions: <Button size="sm">Άνοιγμα</Button>,
  },
  {
    id: '2',
    title: 'Εύρημα ελέγχου κυρώσεων',
    body: 'Πληρωμή ΠΛΗ-2026-0091 · απαιτείται έλεγχος',
    time: 'πριν 12 λεπτά',
    unread: true,
    family: 'warning',
    icon: ShieldAlert,
  },
  {
    id: '3',
    title: 'Αίτημα έγκρισης',
    body: 'Μ. Παπαδοπούλου ζητά έγκριση αποθέματος 6.480,00 €',
    time: '14:02',
    unread: false,
    family: 'brand',
    icon: UserCheck,
    actions: (
      <>
        <Button size="sm" variant="primary">
          Έγκριση
        </Button>
        <Button size="sm" variant="ghost">
          Αναβολή 1 ώρα
        </Button>
      </>
    ),
  },
  { id: '4', title: '5 νέα έγγραφα σε 3 ζημίες', time: 'Χθες', unread: false, icon: FileText },
];

function CentreDemo({ empty = false }: { empty?: boolean }) {
  const [open, setOpen] = useState(false);
  const [items, setItems] = useState(empty ? [] : initial);
  const [signal, setSignal] = useState(0);
  const unread = items.filter((item) => item.unread).length;
  return (
    <div style={{ display: 'flex', gap: 'var(--space-3)', alignItems: 'center' }}>
      <NotificationBell
        count={unread}
        attention={items.some((item) => item.unread && item.family === 'danger')}
        urgentSignal={signal}
        isExpanded={open}
        onPress={() => {
          setOpen((current) => !current);
        }}
      />
      <Button
        size="sm"
        onPress={() => {
          setSignal((value) => value + 1);
        }}
      >
        Επείγον (MI-26)
      </Button>
      <NotificationCenter
        isOpen={open}
        onOpenChange={setOpen}
        notifications={items}
        onMarkAllRead={() => {
          setItems((current) => current.map((item) => ({ ...item, unread: false })));
        }}
      />
    </div>
  );
}

const meta = {
  title: 'Patterns/Notifications',
  component: CentreDemo,
} satisfies Meta<typeof CentreDemo>;

export default meta;
type Story = StoryObj<typeof meta>;

export const BellAndCentre: Story = {};
export const Empty: Story = { args: { empty: true } };

export const BellStates: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-4)' }}>
      <NotificationBell count={0} />
      <NotificationBell count={3} />
      <NotificationBell count={2} attention />
      <NotificationBell count={140} attention />
    </div>
  ),
};
