import type { Meta, StoryObj } from '@storybook/react-vite';

import { Button } from '../Button';
import { Toaster } from './Toast';
import { toast } from './toastStore';

function ToastDemo() {
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 'var(--space-2)' }}>
      <Button
        onPress={() => {
          toast.success({
            title: 'Η εργασία ανατέθηκε',
            description: 'Ανατέθηκε σε Μ. Παπαδοπούλου',
            action: { label: 'Αναίρεση', onAction: () => undefined },
          });
        }}
      >
        Επιτυχία με αναίρεση
      </Button>
      <Button
        onPress={() => {
          toast.info({
            title: 'Η ανανέωση ξεκίνησε',
            description: 'Θα ειδοποιηθείτε όταν ολοκληρωθεί.',
          });
        }}
      >
        Ενημέρωση
      </Button>
      <Button
        onPress={() => {
          toast.warning({ title: 'Το έγγραφο στάλθηκε χωρίς συνημμένα' });
        }}
      >
        Προειδοποίηση
      </Button>
      <Button
        onPress={() => {
          toast.error({
            title: 'Η πληρωμή δεν στάλθηκε',
            description: 'Τα στοιχεία σας αποθηκεύτηκαν. Δοκιμάστε ξανά.',
            action: { label: 'Επανάληψη', onAction: () => undefined },
          });
        }}
      >
        Σφάλμα
      </Button>
      <Button
        onPress={() => {
          const job = toast.progress({ title: 'Έκδοση 48 ασφαλιστηρίων', value: 0 });
          let value = 0;
          const id = setInterval(() => {
            value += 0.25;
            job.update({ value });
            if (value >= 1) {
              clearInterval(id);
              job.success({
                title: 'Εκδόθηκαν 48 ασφαλιστήρια · 2 απέτυχαν',
                action: { label: 'Προβολή', onAction: () => undefined },
              });
            }
          }, 700);
        }}
      >
        Εργασία στο παρασκήνιο
      </Button>
      <Toaster />
    </div>
  );
}

const meta = {
  title: 'Components/Toast',
  component: ToastDemo,
} satisfies Meta<typeof ToastDemo>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};
