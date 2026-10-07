import type { Meta, StoryObj } from '@storybook/react-vite';
import { Copy, FileText, Pencil, Send, Trash2, UserPlus } from 'lucide-react';

import { Button } from '../Button';
import {
  ContextMenu,
  Menu,
  MenuItem,
  MenuSection,
  OverflowMenu,
  SplitButton,
  Submenu,
} from './Menu';

const meta = {
  title: 'Components/Menu',
  component: Menu,
  args: { trigger: <Button>Ενέργειες</Button>, children: null },
  render: (args) => (
    <Menu {...args}>
      <MenuSection title="Εγγραφή">
        <MenuItem id="edit" icon={Pencil} shortcut="Mod+E">
          Επεξεργασία
        </MenuItem>
        <MenuItem id="copy" icon={Copy} description="Νέο πρόχειρο με τα ίδια στοιχεία">
          Αντιγραφή
        </MenuItem>
        <MenuItem
          id="assign"
          icon={UserPlus}
          disabledReason="Η εργασία είναι κλειδωμένη από Μ. Παπαδοπούλου"
        >
          Ανάθεση
        </MenuItem>
        <Submenu label="Αποστολή εγγράφου" icon={Send}>
          <MenuItem id="schedule" icon={FileText}>
            Πίνακας ασφάλισης
          </MenuItem>
          <MenuItem id="green-card" icon={FileText}>
            Πράσινη κάρτα
          </MenuItem>
        </Submenu>
      </MenuSection>
      <MenuSection aria-label="Διαγραφή">
        <MenuItem id="delete" icon={Trash2} isDestructive>
          Διαγραφή πρόχειρου…
        </MenuItem>
      </MenuSection>
    </Menu>
  ),
} satisfies Meta<typeof Menu>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Dropdown: Story = {};

export const Checked: Story = {
  render: () => (
    <Menu trigger={<Button>Πυκνότητα</Button>} selectionMode="single" selectedKeys={['compact']}>
      <MenuItem id="compact">Συμπαγής</MenuItem>
      <MenuItem id="comfortable">Άνετη</MenuItem>
    </Menu>
  ),
};

export const Overflow: Story = {
  render: () => (
    <OverflowMenu>
      <MenuItem id="open">Άνοιγμα</MenuItem>
      <MenuItem id="open-tab">Άνοιγμα σε νέα καρτέλα</MenuItem>
      <MenuItem id="cancel" isDestructive>
        Ακύρωση ασφαλιστηρίου…
      </MenuItem>
    </OverflowMenu>
  ),
};

export const Split: Story = {
  render: () => (
    <SplitButton label="Αποθήκευση" variant="primary" onPress={() => undefined}>
      <MenuItem id="draft">Αποθήκευση πρόχειρου</MenuItem>
      <MenuItem id="close">Αποθήκευση και κλείσιμο</MenuItem>
    </SplitButton>
  ),
};

export const Context: Story = {
  render: () => (
    <ContextMenu
      aria-label="Ενέργειες γραμμής"
      items={
        <>
          <MenuItem id="open">Άνοιγμα</MenuItem>
          <MenuItem id="assign">Ανάθεση</MenuItem>
          <MenuItem id="cancel" isDestructive>
            Ακύρωση…
          </MenuItem>
        </>
      }
    >
      <div
        tabIndex={0}
        style={{
          padding: 'var(--space-4)',
          border: 'var(--border-width-hairline) dashed var(--color-border-default)',
        }}
      >
        ΑΣΦ-2026-004471 · δεξί κλικ ή Shift+F10
      </div>
    </ContextMenu>
  ),
};
