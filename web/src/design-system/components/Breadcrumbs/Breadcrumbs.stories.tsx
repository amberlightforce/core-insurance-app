import type { Meta, StoryObj } from '@storybook/react-vite';

import { Breadcrumbs } from './Breadcrumbs';

const meta = {
  title: 'Components/Breadcrumbs',
  component: Breadcrumbs,
  args: {
    items: [
      { id: 'home', label: 'Αρχική', href: '#' },
      { id: 'policies', label: 'Ασφαλιστήρια', href: '#' },
      { id: 'policy', label: 'ΑΣΦ-2026-000412' },
    ],
  },
} satisfies Meta<typeof Breadcrumbs>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Short: Story = {};

/** More than four items: the middle collapses into a ⋯ menu. */
export const Collapsed: Story = {
  args: {
    items: [
      { id: 'home', label: 'Αρχική', href: '#' },
      { id: 'claims', label: 'Ζημίες', href: '#' },
      { id: 'claim', label: 'ΖΗΜ-2026-001877 · Παπαδόπουλος Γεώργιος', href: '#' },
      { id: 'exposure', label: 'Έκθεση 02 · Υλικές ζημιές', href: '#' },
      { id: 'payments', label: 'Πληρωμές', href: '#' },
      { id: 'payment', label: 'Πληρωμή 3' },
    ],
  },
};
