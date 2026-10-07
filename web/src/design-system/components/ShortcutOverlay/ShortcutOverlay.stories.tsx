import type { Meta, StoryObj } from '@storybook/react-vite';

import { ShortcutOverlay } from './ShortcutOverlay';

const meta = {
  title: 'Patterns/ShortcutOverlay',
  component: ShortcutOverlay,
  args: {
    groups: [
      {
        title: 'Γενικά',
        shortcuts: [
          { keys: 'Mod+K', description: 'Παλέτα εντολών' },
          { keys: '?', description: 'Συντομεύσεις πληκτρολογίου' },
          { keys: 'F6', description: 'Επόμενη περιοχή' },
          { keys: 'F8', description: 'Μετάβαση στις ειδοποιήσεις' },
        ],
      },
      {
        title: 'Ουρά εργασιών',
        shortcuts: [
          { keys: 'J', description: 'Επόμενη εργασία' },
          { keys: 'K', description: 'Προηγούμενη εργασία' },
          { keys: 'X', description: 'Επιλογή εργασίας' },
          { keys: 'A', description: 'Έγκριση' },
        ],
      },
      {
        title: 'Οδηγός',
        shortcuts: [
          { keys: 'Alt+→', description: 'Επόμενο βήμα' },
          { keys: 'Alt+←', description: 'Προηγούμενο βήμα' },
          { keys: 'Mod+Enter', description: 'Δέσμευση' },
        ],
      },
    ],
  },
  parameters: {
    docs: { description: { component: 'Πατήστε ? εκτός πεδίου κειμένου για να ανοίξει.' } },
  },
} satisfies Meta<typeof ShortcutOverlay>;

export default meta;
type Story = StoryObj<typeof meta>;

export const PressQuestionMark: Story = {};
export const Open: Story = { args: { isOpen: true } };
