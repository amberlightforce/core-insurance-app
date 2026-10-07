import type { Meta, StoryObj } from '@storybook/react-vite';

import { Tabs } from './Tabs';
import type { TabItem } from './tabsLayout';

const policyTabs: TabItem[] = [
  { id: 'summary', label: 'Σύνοψη' },
  { id: 'coverages', label: 'Καλύψεις', errorCount: 2 },
  { id: 'transactions', label: 'Συναλλαγές', count: 4 },
  { id: 'charges', label: 'Χρεώσεις', disabledReason: 'Δεν έχετε δικαίωμα προβολής χρεώσεων' },
  { id: 'claims', label: 'Ζημίες', count: 3 },
  { id: 'documents', label: 'Έγγραφα', count: 12 },
  { id: 'history', label: 'Ιστορικό' },
];

const meta = {
  title: 'Components/Tabs',
  component: Tabs,
  args: {
    items: policyTabs,
    'aria-label': 'Ασφαλιστήριο ΑΣΦ-2026-000412',
    variant: 'line',
    children: (item: TabItem) => (
      <p style={{ margin: 0 }}>
        Περιεχόμενο καρτέλας «{item.label}». Ctrl+PgUp/PgDn αλλάζει καρτέλα από εδώ.
      </p>
    ),
  },
  argTypes: {
    variant: { control: 'inline-radio', options: ['line', 'contained'] },
    manual: { control: 'boolean' },
    maxVisible: { control: { type: 'number', min: 1, max: 7 } },
  },
} satisfies Meta<typeof Tabs>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Line: Story = {};

export const Contained: Story = {
  args: {
    variant: 'contained',
    items: [
      { id: 'monthly', label: 'Μηνιαία' },
      { id: 'quarterly', label: 'Τριμηνιαία' },
      { id: 'yearly', label: 'Ετήσια' },
    ],
    'aria-label': 'Περίοδος',
  },
};

/** Overflow into «Περισσότερα ▾»: narrow the container or set `maxVisible`. */
export const Overflow: Story = {
  args: { maxVisible: 4, defaultSelectedKey: 'history' },
  render: (args) => (
    <div style={{ maxInlineSize: 520 }}>
      <Tabs {...args} />
    </div>
  ),
};

/** Manual activation for expensive panels (arrows move focus, Enter selects). */
export const Manual: Story = { args: { manual: true } };
