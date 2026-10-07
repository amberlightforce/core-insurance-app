import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState } from 'react';

import { Button } from '../Button';
import { SideSheet, type SideSheetProps } from './SideSheet';

function SheetDemo(props: Partial<SideSheetProps>) {
  const [open, setOpen] = useState(false);
  const [plate, setPlate] = useState('');
  return (
    <div
      style={{
        position: 'relative',
        minHeight: 480,
        padding: 'var(--space-4)',
        background: 'var(--color-surface-sheet)',
        overflow: 'hidden',
      }}
    >
      <p>Ασφαλιστήριο ΑΣΦ-2026-004471 · Αυτοκίνητο ΙΧ</p>
      <Button
        onPress={() => {
          setOpen(true);
        }}
      >
        Προσθήκη οχήματος
      </Button>
      <SideSheet
        isOpen={open}
        onClose={() => {
          setOpen(false);
          setPlate('');
        }}
        title="Νέο όχημα"
        context="ΑΣΦ-2026-004471"
        isDirty={plate !== ''}
        changedFields={plate ? ['Αριθμός κυκλοφορίας'] : []}
        onSave={() =>
          new Promise((resolve) => {
            setTimeout(resolve, 800);
          })
        }
        {...props}
      >
        <label style={{ display: 'grid', gap: 'var(--space-1)' }}>
          Αριθμός κυκλοφορίας
          <input
            value={plate}
            onChange={(event) => {
              setPlate(event.target.value);
            }}
          />
        </label>
      </SideSheet>
    </div>
  );
}

const meta = {
  title: 'Components/SideSheet',
  component: SheetDemo,
  args: { size: 'md' },
  argTypes: { size: { control: 'inline-radio', options: ['md', 'lg', 'split'] } },
} satisfies Meta<typeof SheetDemo>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Default: Story = {};
export const Large: Story = { args: { size: 'lg' } };
export const Split: Story = { args: { size: 'split' } };
export const WithError: Story = {
  args: { error: 'Ο αριθμός κυκλοφορίας υπάρχει ήδη σε άλλο ασφαλιστήριο.' },
};
export const Busy: Story = { args: { isBusy: true } };
export const ReadOnly: Story = { args: { isReadOnly: true, onEdit: () => undefined } };
