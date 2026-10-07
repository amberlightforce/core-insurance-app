import type { Meta, StoryObj } from '@storybook/react-vite';
import { FilePen } from 'lucide-react';
import { DialogTrigger } from 'react-aria-components';

import { Button } from '../Button';
import { ConfirmDialog } from './ConfirmDialog';
import { Dialog } from './Dialog';
import { UnsavedChangesDialog } from './UnsavedChangesDialog';

const meta = {
  title: 'Components/Dialog',
  component: Dialog,
  args: {
    title: 'Νέα σημείωση',
    icon: FilePen,
    size: 'md',
    primaryAction: { label: 'Αποθήκευση', onAction: () => undefined },
  },
  argTypes: { size: { control: 'inline-radio', options: ['sm', 'md', 'lg', 'xl'] } },
  render: (args) => (
    <DialogTrigger>
      <Button>Άνοιγμα διαλόγου</Button>
      <Dialog {...args}>
        <label style={{ display: 'grid', gap: 'var(--space-1)' }}>
          Κείμενο σημείωσης
          <textarea rows={4} />
        </label>
      </Dialog>
    </DialogTrigger>
  ),
} satisfies Meta<typeof Dialog>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};

export const Busy: Story = {
  args: {
    primaryAction: {
      label: 'Αποθήκευση',
      onAction: () =>
        new Promise((resolve) => {
          setTimeout(resolve, 2000);
        }),
    },
  },
};

export const WithError: Story = {
  args: { error: 'Η σημείωση δεν αποθηκεύτηκε. Τα στοιχεία σας διατηρήθηκαν.' },
};

export const ConfirmLevel1: Story = {
  render: () => (
    <DialogTrigger>
      <Button variant="danger-ghost">Απόσυρση προσφοράς…</Button>
      <ConfirmDialog
        level={1}
        title="Απόσυρση προσφοράς ΠΡΦ-2026-0412;"
        consequence="Η προσφορά δεν θα μπορεί πλέον να δεσμευτεί."
        confirmLabel="Απόσυρση προσφοράς"
        onConfirm={() => undefined}
      />
    </DialogTrigger>
  ),
};

export const ConfirmLevel2: Story = {
  render: () => (
    <DialogTrigger>
      <Button variant="danger-ghost">Ακύρωση ασφαλιστηρίου…</Button>
      <ConfirmDialog
        level={2}
        title="Ακύρωση ασφαλιστηρίου ΑΣΦ-2026-004471;"
        consequence="Το ασφαλιστήριο θα ακυρωθεί από 15/10/2026. Θα εκδοθεί επιστροφή 128,40 €."
        consequences={[
          { label: 'Ημερομηνία ισχύος', value: '15/10/2026' },
          { label: 'Επιστροφή ασφαλίστρου', value: '128,40 €' },
          { label: 'Έγγραφα', value: 'Πράξη ακύρωσης' },
          { label: 'Ειδοποιήσεις', value: 'Πελάτης, παραγωγός' },
        ]}
        reasons={[
          { id: 'customer', label: 'Αίτημα πελάτη' },
          { id: 'non-payment', label: 'Μη πληρωμή ασφαλίστρου' },
          { id: 'sale', label: 'Πώληση οχήματος' },
        ]}
        confirmToken="4471"
        confirmLabel="Ακύρωση ασφαλιστηρίου"
        onConfirm={() => undefined}
      />
    </DialogTrigger>
  ),
};

export const ConfirmLevel3: Story = {
  render: () => (
    <DialogTrigger>
      <Button variant="danger-ghost">Συγχώνευση προσώπων…</Button>
      <ConfirmDialog
        level={3}
        title="Συγχώνευση προσώπων ΠΡΣ-1182 και ΠΡΣ-2040;"
        consequences={[
          { label: 'Ασφαλιστήρια σε ισχύ', value: '3' },
          { label: 'Επιζών', value: 'ΠΡΣ-1182' },
        ]}
        reasons={[{ id: 'duplicate', label: 'Διπλή εγγραφή' }]}
        confirmToken="2040"
        approverGroup="Διαχείριση δεδομένων"
        confirmLabel="Συγχώνευση προσώπων"
        onConfirm={() => undefined}
      />
    </DialogTrigger>
  ),
};

export const UnsavedChanges: Story = {
  render: () => (
    <DialogTrigger>
      <Button>Έξοδος</Button>
      <UnsavedChangesDialog
        changedFields={[
          'Οδηγός',
          'Ημερομηνία ζημίας',
          'Ποσό',
          'Συνεργείο',
          'Σημείωση',
          'Τόπος',
          'Ώρα',
        ]}
        onDiscard={() => undefined}
        onSave={() => undefined}
      />
    </DialogTrigger>
  ),
};
