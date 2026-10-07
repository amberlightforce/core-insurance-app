import type { Meta, StoryObj } from '@storybook/react-vite';
import { Banknote, FileCheck } from 'lucide-react';

import { Timeline, type TimelineEvent } from './index';

const now = new Date(2026, 9, 7, 14, 30);
const maria = { kind: 'user' as const, id: 'u1', name: 'Μαρία Παπαδοπούλου' };

const events: TimelineEvent[] = [
  {
    id: 'n',
    at: new Date(2026, 9, 7, 14, 29),
    actor: { kind: 'system' },
    action: 'κατέγραψε είσπραξη',
    object: { label: 'ΠΛΗ-2026-1188', href: '#payment' },
    category: 'transactions',
    icon: Banknote,
    family: 'success',
    isNew: true,
  },
  {
    id: 'e1',
    at: new Date(2026, 9, 7, 14, 25),
    actor: maria,
    action: 'άλλαξε',
    object: { label: 'ΑΣΦ-2026-0412', href: '#policy' },
    category: 'fields',
    diffs: [{ field: 'Ημερομηνία λήξης', from: '31/12/2026', to: '30/06/2027' }],
  },
  {
    id: 'e1b',
    at: new Date(2026, 9, 7, 14, 23),
    actor: maria,
    action: 'άλλαξε',
    category: 'fields',
    diffs: [{ field: 'Απαλλαγή', from: '300,00 €', to: '500,00 €' }],
  },
  {
    id: 'e2',
    at: new Date(2026, 9, 7, 9, 0),
    actor: { kind: 'system' },
    action: 'εξέδωσε',
    object: { label: 'Ειδοποίηση ανανέωσης' },
    category: 'documents',
    icon: FileCheck,
    family: 'brand',
    trace: { label: 'trace 4f2a…91c0', href: '#trace' },
  },
  {
    id: 'e3',
    at: new Date(2026, 9, 6, 16, 0),
    actor: { kind: 'agent', onBehalfOf: 'Μ. Π.' },
    action: 'πρότεινε απόθεμα',
    category: 'ai',
    family: 'ai',
  },
  {
    id: 'e4',
    at: new Date(2026, 9, 5, 11, 0),
    actor: { kind: 'user', id: 'u2', name: 'Κώστας Νικολάου' },
    action: 'τηλεφώνησε στον πελάτη',
    category: 'communication',
    family: 'teal',
  },
];

const meta = {
  title: 'Components/Timeline',
  component: Timeline,
  args: {
    'aria-label': 'Ιστορικό ασφαλιστηρίου',
    events,
    now,
    hasMore: true,
    onLoadOlder: () => undefined,
  },
  argTypes: { state: { control: 'inline-radio', options: ['ready', 'loading', 'error'] } },
} satisfies Meta<typeof Timeline>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};
export const LoadingOlder: Story = { args: { isLoadingOlder: true } };
export const Empty: Story = { args: { events: [], defaultFilter: 'documents' } };
export const Loading: Story = { args: { state: 'loading' } };
export const LoadFailed: Story = { args: { state: 'error', onRetry: () => undefined } };
