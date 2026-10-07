import type { Meta, StoryObj } from '@storybook/react-vite';

import { ApprovalPanel } from './ApprovalPanel';

const meta = {
  title: 'Patterns/Approval panel (maker-checker)',
  component: ApprovalPanel,
  args: {
    title: 'Πληρωμή αποζημίωσης · ΖΗΜ-2026-000318',
    maker: { id: 'u-maker', name: 'Μαρία Παπαδοπούλου', role: 'Χειρίστρια ζημιών' },
    submittedAt: '2026-10-07T11:02:00Z',
    justification: 'Συμφωνημένη προσφορά με τον ζημιωθέντα, βάσει πραγματογνωμοσύνης.',
    checkerGroup: 'Ομάδα Πληρωμών Β',
    currentUserId: 'u-checker',
    amount: { value: '6480.00' },
    authorityLimit: '60000',
    isPayment: true,
    changes: [
      { id: 'r', label: 'Απόθεμα · Υλικές ζημιές', from: '5.220,00 €', to: '6.480,00 €' },
      { id: 'p', label: 'Δικαιούχος', from: '—', to: 'Νικολάου Κώστας · GR16 •••• 0695' },
    ],
    onApprove: () => undefined,
    onReject: () => undefined,
    onReturn: () => undefined,
    onEscalate: () => undefined,
  },
} satisfies Meta<typeof ApprovalPanel>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Checker: Story = {};
export const OverAuthority: Story = { args: { amount: { value: '75000.00' } } };
export const OwnAction: Story = { args: { currentUserId: 'u-maker' } };
