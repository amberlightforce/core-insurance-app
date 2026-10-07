import type { Meta, StoryObj } from '@storybook/react-vite';
import { FilePlus, FolderOpen, Megaphone, Shield, UserPlus, Users } from 'lucide-react';
import { useState } from 'react';

import { Button } from '../Button';
import { CommandPalette } from './CommandPalette';
import type { PaletteItem } from './paletteQuery';
import { useCommandPaletteShortcut } from './useCommandPaletteShortcut';

const noop = () => undefined;

const items: PaletteItem[] = [
  {
    id: 'new-quote',
    group: 'actions',
    title: 'Νέα προσφορά',
    icon: FilePlus,
    shortcut: 'Q',
    onAction: noop,
  },
  {
    id: 'new-fnol',
    group: 'actions',
    title: 'Νέα αναγγελία ζημίας',
    icon: Megaphone,
    shortcut: 'N Z',
    onAction: noop,
  },
  { id: 'assign', group: 'actions', title: 'Ανάθεση σε…', icon: UserPlus, onAction: noop },
  {
    id: 'go-policies',
    group: 'navigation',
    title: 'Ασφαλιστήρια',
    icon: Shield,
    shortcut: 'G P',
    onAction: noop,
  },
  {
    id: 'go-parties',
    group: 'navigation',
    title: 'Πρόσωπα',
    icon: Users,
    shortcut: 'G H',
    onAction: noop,
  },
  {
    id: 'pol-4471',
    group: 'records',
    kind: 'policy',
    code: 'ΑΣΦ-2026-004471',
    title: 'Μαρία Παπαδοπούλου',
    subtitle: 'ΑΣΦ-2026-004471 · Σε ισχύ · Αυτοκίνητο ΙΧ',
    icon: Shield,
    onAction: noop,
  },
  {
    id: 'clm-91',
    group: 'records',
    kind: 'claim',
    code: 'ΖΗΜ-2026-000091',
    title: 'Κώστας Νικολάου',
    subtitle: 'ΖΗΜ-2026-000091 · Ανοιχτή · Υλικές ζημίες',
    icon: FolderOpen,
    onAction: noop,
  },
  {
    id: 'r1',
    group: 'recent',
    title: 'Ελένη Γεωργίου',
    subtitle: 'ΠΡΣ-1182 · Πελάτης',
    icon: Users,
    onAction: noop,
  },
  {
    id: 'r2',
    group: 'recent',
    title: 'Ανανέωση ΑΣΦ-2025-003310',
    subtitle: 'Εργασία',
    onAction: noop,
  },
];

function PaletteDemo({ failSearch = false }: { failSearch?: boolean }) {
  const [open, setOpen] = useState(false);
  useCommandPaletteShortcut(() => {
    setOpen((current) => !current);
  });
  return (
    <>
      <Button
        shortcut="Mod+K"
        onPress={() => {
          setOpen(true);
        }}
      >
        Αναζήτηση ή εντολή…
      </Button>
      <CommandPalette
        isOpen={open}
        onOpenChange={setOpen}
        items={items}
        onSearch={(query) =>
          new Promise((resolve, reject) => {
            setTimeout(() => {
              if (failSearch) reject(new Error('offline'));
              else
                resolve(
                  query.length > 2
                    ? [
                        {
                          id: `remote-${query}`,
                          group: 'records',
                          kind: 'customer',
                          title: 'Νίκος Δημητρίου',
                          subtitle: 'ΠΡΣ-2040 · Πελάτης',
                          onAction: noop,
                        },
                      ]
                    : [],
                );
            }, 250);
          })
        }
        onOpenInNewTab={noop}
        onFullTextSearch={noop}
      />
    </>
  );
}

const meta = {
  title: 'Patterns/CommandPalette',
  component: PaletteDemo,
} satisfies Meta<typeof PaletteDemo>;

export default meta;
type Story = StoryObj<typeof meta>;

export const Playground: Story = {};
export const SearchUnavailable: Story = { args: { failSearch: true } };
