import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import { Button } from '../Button';
import { Banner } from './Banner';

const meta = {
  title: 'Components/Banner',
  component: Banner,
  args: {
    variant: 'warning',
    title: 'Υπάρχουν νεότερα δεδομένα',
    children: 'Η εγγραφή άλλαξε από άλλο χρήστη στις 14:02.',
    layout: 'inline',
  },
  argTypes: {
    variant: {
      control: 'select',
      options: ['info', 'success', 'warning', 'danger', 'ai', 'system'],
    },
    layout: { control: 'inline-radio', options: ['inline', 'page'] },
  },
} satisfies Meta<typeof Banner>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const Variants: Story = {
  render: () => (
    <div style={{ display: 'grid', gap: 'var(--space-3)', maxWidth: 640 }}>
      <Banner variant="info" title="Η ανανέωση ξεκινά σε 30 ημέρες" live="none">
        Θα σταλεί ειδοποίηση στον πελάτη στις 15/10/2026.
      </Banner>
      <Banner variant="success" title="Η πληρωμή εγκρίθηκε" live="none">
        Εγκρίθηκε από Μ. Παπαδοπούλου · 14:32
      </Banner>
      <Banner
        variant="warning"
        title="Υπάρχουν νεότερα δεδομένα"
        live="none"
        actions={<Button variant="link">Ανανέωση</Button>}
      />
      <Banner variant="danger" title="Διορθώστε 3 πεδία για να συνεχίσετε" live="none">
        Ημερομηνία ζημίας · ΑΦΜ · Αριθμός κυκλοφορίας
      </Banner>
      <Banner
        variant="ai"
        title="Η βοήθεια ΤΝ δεν είναι διαθέσιμη· συνεχίστε χειροκίνητα"
        live="none"
      />
      <Banner
        variant="system"
        layout="page"
        title="Προγραμματισμένη συντήρηση σήμερα 22:00–23:00"
        live="none"
      />
    </div>
  ),
};

function DismissDemo() {
  const [shown, setShown] = useState(true);
  return shown ? (
    <Banner
      variant="info"
      title="Νέα λειτουργία: αποθηκευμένες προβολές"
      onDismiss={() => {
        setShown(false);
      }}
    >
      Αποθηκεύστε φίλτρα και στήλες για να επιστρέφετε με ένα κλικ.
    </Banner>
  ) : (
    <Button
      onPress={() => {
        setShown(true);
      }}
    >
      Εμφάνιση ξανά
    </Button>
  );
}

export const Dismissible: Story = { render: () => <DismissDemo /> };
