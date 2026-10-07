import type { Meta, StoryObj } from '@storybook/react-vite';

import { ErrorSummary } from './ErrorSummary';

const meta = {
  title: 'Components/ErrorSummary',
  component: ErrorSummary,
  args: {
    errors: [
      { fieldId: 'loss-date', message: 'Συμπληρώστε την ημερομηνία ζημίας.' },
      { fieldId: 'afm', message: 'Ο ΑΦΜ πρέπει να έχει 9 ψηφία. Ελέγξτε τον αριθμό.' },
      {
        fieldId: 'plate',
        message:
          'Η πινακίδα πρέπει να έχει 3 γράμματα και 4 ψηφία (ΑΑΑ-0000). Ελέγξτε την πινακίδα.',
      },
    ],
  },
} satisfies Meta<typeof ErrorSummary>;

export default meta;
type Story = StoryObj<typeof meta>;

export const ThreeErrors: Story = {};

export const OneError: Story = {
  args: { errors: [{ fieldId: 'loss-date', message: 'Συμπληρώστε την ημερομηνία ζημίας.' }] },
};
