import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import { SplitView } from './SplitView';

const referrals = [
  { id: 'ΠΑΡ-2026-0412', name: 'Παπαδόπουλος Γεώργιος', product: 'Αυτοκίνητο ΙΧ' },
  { id: 'ΠΑΡ-2026-0413', name: 'Νικολάου Μαρία', product: 'Κατοικία' },
  { id: 'ΠΑΡ-2026-0414', name: 'Αλεξίου Δημήτρης', product: 'Αυτοκίνητο ΙΧ' },
];

function Demo() {
  const [selected, setSelected] = useState<string | null>(null);
  const current = referrals.find((r) => r.id === selected);
  return (
    <div style={{ blockSize: '480px' }}>
      <SplitView
        listLabel="Οι παραπομπές μου"
        detailLabel="Λεπτομέρειες παραπομπής"
        isDetailOpen={selected !== null}
        onCloseDetail={() => {
          setSelected(null);
        }}
        list={
          <ul>
            {referrals.map((r) => (
              <li key={r.id}>
                <button
                  type="button"
                  onClick={() => {
                    setSelected(r.id);
                  }}
                >
                  {r.name} · {r.id}
                </button>
              </li>
            ))}
          </ul>
        }
        detail={
          current ? (
            <div style={{ padding: 'var(--space-4)' }}>
              <h2 className="ds-heading-2">{current.name}</h2>
              <p className="ds-mono">{current.id}</p>
              <p>{current.product}</p>
            </div>
          ) : (
            <p style={{ padding: 'var(--space-4)' }}>Επιλέξτε μια παραπομπή από τη λίστα.</p>
          )
        }
      />
    </div>
  );
}

const meta = {
  title: 'Patterns/Workbench split view',
  component: SplitView,
  render: () => <Demo />,
  args: {
    listLabel: 'Οι παραπομπές μου',
    detailLabel: 'Λεπτομέρειες παραπομπής',
    list: null,
    detail: null,
    isDetailOpen: false,
    onCloseDetail: () => undefined,
  },
} satisfies Meta<typeof SplitView>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};
