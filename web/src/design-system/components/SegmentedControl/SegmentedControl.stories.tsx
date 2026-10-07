import type { Meta, StoryObj } from '@storybook/react-vite';
import { List, Map as MapIcon, Table } from 'lucide-react';

import { SegmentedControl } from './SegmentedControl';

const meta = {
  title: 'Components/SegmentedControl',
  component: SegmentedControl,
  args: {
    label: 'Περίοδος',
    options: [
      { id: 'month', label: 'Μηνιαία' },
      { id: 'quarter', label: 'Τριμηνιαία' },
      { id: 'year', label: 'Ετήσια' },
    ],
  },
  argTypes: { size: { control: 'inline-radio', options: ['sm', 'md', 'lg'] } },
} satisfies Meta<typeof SegmentedControl>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const WithIconsAndDisabledSegment: Story = {
  args: {
    label: 'Προβολή',
    options: [
      { id: 'list', label: 'Λίστα', icon: List },
      { id: 'table', label: 'Πίνακας', icon: Table },
      {
        id: 'map',
        label: 'Χάρτης',
        icon: MapIcon,
        disabledReason: 'Ο χάρτης δεν είναι διαθέσιμος για αυτή την ουρά.',
      },
    ],
  },
};

export const Sizes: Story = {
  render: (args) => (
    <div style={{ display: 'grid', gap: 'var(--space-4)', justifyItems: 'start' }}>
      <SegmentedControl {...args} label="Μικρό" size="sm" />
      <SegmentedControl {...args} label="Μεσαίο" size="md" />
      <SegmentedControl {...args} label="Μεγάλο" size="lg" />
    </div>
  ),
};

export const YesNo: Story = {
  args: {
    label: 'Έχει γίνει ζημία τα τελευταία 5 χρόνια;',
    options: [
      { id: 'yes', label: 'Ναι' },
      { id: 'no', label: 'Όχι' },
    ],
    defaultValue: 'no',
  },
};

export const ReadOnly: Story = { args: { isReadOnly: true, value: 'quarter' } };
