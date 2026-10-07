import type { Meta, StoryObj } from '@storybook/react-vite';

import { Avatar, FieldLock, PresenceStack, type PresencePerson } from './Avatar';

const meta = {
  title: 'Components/Avatar',
  component: Avatar,
  args: { name: 'Μαρία Παπαδοπούλου', size: 'md' },
  argTypes: {
    size: { control: 'inline-radio', options: ['xs', 'sm', 'md', 'lg', 'xl'] },
    presence: { control: 'inline-radio', options: [undefined, 'viewing', 'editing', 'idle'] },
    kind: { control: 'inline-radio', options: ['person', 'organisation'] },
  },
} satisfies Meta<typeof Avatar>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const Sizes: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-4)', alignItems: 'center' }}>
      <Avatar name="Κώστας Νικολάου" size="xs" />
      <Avatar name="Ελένη Γεωργίου" size="sm" />
      <Avatar name="Μαρία Παπαδοπούλου" size="md" />
      <Avatar name="Ασφαλιστική Αιγαίου ΑΕ" size="lg" kind="organisation" />
      <Avatar name="Γιώργος Αντωνίου" size="xl" />
    </div>
  ),
};

export const PresenceRings: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 'var(--space-4)', alignItems: 'center' }}>
      <Avatar name="Μαρία Παπαδοπούλου" presence="viewing" />
      <Avatar name="Κώστας Νικολάου" presence="editing" />
      <Avatar name="Ελένη Γεωργίου" presence="idle" />
      <Avatar name="Νίκος Δημητρίου" status="success" statusLabel="Διαθέσιμος" />
    </div>
  ),
};

const people: PresencePerson[] = [
  { id: '1', name: 'Μαρία Παπαδοπούλου', presence: 'editing', section: 'Καλύψεις', since: '14:02' },
  { id: '2', name: 'Κώστας Νικολάου', presence: 'viewing', since: '13:48' },
  { id: '3', name: 'Ελένη Γεωργίου', presence: 'viewing', section: 'Έγγραφα', since: '13:55' },
  { id: '4', name: 'Νίκος Δημητρίου', presence: 'idle', since: '12:10' },
  { id: '5', name: 'Άννα Σταυροπούλου', presence: 'viewing', since: '14:05' },
  { id: '6', name: 'Παύλος Ιωάννου', presence: 'viewing', since: '14:06' },
  { id: '7', name: 'Σοφία Κωνσταντίνου', presence: 'idle', since: '11:30' },
];

export const Stack: Story = { render: () => <PresenceStack people={people} /> };

export const Lock: Story = {
  render: () => <FieldLock name="Μ. Παπαδοπούλου" gender="female" />,
};
