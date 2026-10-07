import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import type { StepItem } from '../../components/Stepper';
import { TextField } from '../../components/TextField';
import { Wizard } from './Wizard';

const steps: StepItem[] = [
  {
    id: 'policyholder',
    label: 'Λήπτης ασφάλισης',
    state: 'complete',
    subLabel: 'Παπαδόπουλος Γεώργιος',
  },
  { id: 'vehicles', label: 'Οχήματα', state: 'complete', subLabel: '1 όχημα · 2 οδηγοί' },
  { id: 'cover', label: 'Καλύψεις', state: 'warning', subLabel: '1 ζήτημα ανάληψης' },
  { id: 'review', label: 'Έλεγχος και δέσμευση', state: 'upcoming' },
];

function Demo({ blocked }: { blocked: boolean }) {
  const [current, setCurrent] = useState('vehicles');
  return (
    <Wizard
      title="Νέα προσφορά · Αυτοκίνητο ΙΧ"
      steps={steps}
      currentId={current}
      onNavigate={setCurrent}
      onSaveDraft={() => undefined}
      lastSavedAt={new Date()}
      commit={{
        label: 'Δέσμευση',
        onCommit: () => undefined,
        ...(blocked
          ? { disabledReason: 'Δεν μπορεί να γίνει δέσμευση: 1 ανοιχτό ζήτημα ανάληψης' }
          : {}),
      }}
      summary={
        <div>
          <p className="ds-caption">Συνολικό ασφάλιστρο</p>
          <p className="ds-heading-2 ds-num">412,38 €</p>
        </div>
      }
    >
      <TextField label="Αριθμός κυκλοφορίας" mono />
      <TextField label="Αριθμός πλαισίου (VIN)" mono />
    </Wizard>
  );
}

const meta = {
  title: 'Patterns/Wizard',
  component: Wizard,
  render: () => <Demo blocked={false} />,
  args: {
    title: 'Νέα προσφορά',
    steps,
    currentId: 'vehicles',
    onNavigate: () => undefined,
    children: null,
    commit: { label: 'Δέσμευση', onCommit: () => undefined },
  },
} satisfies Meta<typeof Wizard>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};
export const BlockedCommit: Story = { render: () => <Demo blocked /> };
