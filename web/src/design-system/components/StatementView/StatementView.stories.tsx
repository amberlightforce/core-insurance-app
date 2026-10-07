import type { Meta, StoryObj } from '@storybook/react-vite';

import { StatementView, type StatementLine } from './index';

const lines: StatementLine[] = [
  {
    id: '1',
    description: 'Προμήθεια Σεπτεμβρίου',
    amount: 1250,
    date: new Date(2026, 8, 30),
    reference: 'ΠΡΜ-0912',
    href: '#c1',
  },
  {
    id: '2',
    description: 'Αντιλογισμός ακύρωσης ΑΣΦ-2026-0311',
    amount: -210.5,
    date: new Date(2026, 9, 2),
    reference: 'ΑΚΥ-0044',
    href: '#c2',
  },
  {
    id: '3',
    description: 'Μπόνους παραγωγής Q3',
    amount: 400,
    date: new Date(2026, 9, 3),
    reference: 'ΜΠΝ-0003',
  },
  { id: '4', description: 'Σύνολο περιόδου', amount: 1439.5, kind: 'subtotal' },
];

const meta = {
  title: 'Components/StatementView',
  component: StatementView,
  args: {
    title: 'Κατάσταση προμηθειών',
    balance: 1439.5,
    direction: 'payable',
    lines,
    adverseRule: 'negative',
  },
  argTypes: {
    state: { control: 'inline-radio', options: ['ready', 'loading', 'empty', 'error'] },
    direction: { control: 'inline-radio', options: ['receivable', 'payable', 'settled'] },
    adverseRule: { control: 'inline-radio', options: ['negative', 'positive', 'none'] },
  },
} satisfies Meta<typeof StatementView>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const RunningBalance: Story = {
  args: {
    showRunningBalance: true,
    openingBalance: 500,
    direction: 'receivable',
    title: 'Λογαριασμός πελάτη',
  },
};

export const Settled: Story = {
  args: { balance: 0, direction: 'settled' },
};

export const Loading: Story = { args: { state: 'loading' } };
export const Empty: Story = { args: { lines: [], state: 'empty' } };
export const LoadFailed: Story = {
  args: { state: 'error', onRetry: () => undefined, correlationId: '4f2a91c0' },
};
