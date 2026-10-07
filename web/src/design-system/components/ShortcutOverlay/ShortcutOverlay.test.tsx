import { screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { ShortcutOverlay, type ShortcutGroup } from './ShortcutOverlay';

const groups: ShortcutGroup[] = [
  {
    title: 'Γενικά',
    shortcuts: [
      { keys: 'Mod+K', description: 'Παλέτα εντολών' },
      { keys: 'F8', description: 'Μετάβαση στις ειδοποιήσεις' },
    ],
  },
  {
    title: 'Ουρά εργασιών',
    shortcuts: [
      { keys: 'J', description: 'Επόμενη εργασία' },
      { keys: 'K', description: 'Προηγούμενη εργασία' },
    ],
  },
];

describe('ShortcutOverlay', () => {
  it('opens with ? outside text fields and focuses the search', async () => {
    const { user } = renderWithDs(
      <>
        <button type="button">Εργασία</button>
        <ShortcutOverlay groups={groups} />
      </>,
    );
    await user.click(screen.getByRole('button', { name: 'Εργασία' }));
    await user.keyboard('?');
    const dialog = await screen.findByRole('dialog', { name: 'Συντομεύσεις πληκτρολογίου' });
    await waitFor(() => {
      expect(
        within(dialog).getByRole('searchbox', { name: 'Αναζήτηση συντόμευσης' }),
      ).toHaveFocus();
    });
    expect(within(dialog).getByRole('heading', { name: 'Γενικά' })).toBeInTheDocument();
    expect(within(dialog).getAllByRole('listitem')).toHaveLength(4);
    expect(within(dialog).getByText('Παλέτα εντολών').parentElement).toHaveTextContent('Ctrl');
  });

  it('ignores ? inside text fields', async () => {
    const { user } = renderWithDs(
      <>
        <input aria-label="Σημείωση" />
        <ShortcutOverlay groups={groups} />
      </>,
    );
    await user.click(screen.getByRole('textbox', { name: 'Σημείωση' }));
    await user.keyboard('?');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('respects the single-key shortcuts preference', async () => {
    const { user } = renderWithDs(<ShortcutOverlay groups={groups} />, {
      preferences: { singleKeyShortcuts: false },
    });
    await user.keyboard('?');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('filters accent-insensitively and shows an empty message', async () => {
    const { user } = renderWithDs(<ShortcutOverlay groups={groups} isOpen />);
    const search = await screen.findByRole('searchbox');
    await user.type(search, 'εργασια');
    const dialog = screen.getByRole('dialog');
    expect(within(dialog).getAllByRole('listitem')).toHaveLength(2);
    expect(within(dialog).queryByRole('heading', { name: 'Γενικά' })).not.toBeInTheDocument();
    await user.clear(search);
    await user.type(search, 'zzz');
    expect(within(dialog).getByRole('status')).toHaveTextContent('Καμία συντόμευση για «zzz»');
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<ShortcutOverlay groups={groups} isOpen />);
    expect(await screen.findByRole('dialog', { name: 'Keyboard shortcuts' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderWithDs(<ShortcutOverlay groups={groups} isOpen />);
    const dialog = await screen.findByRole('dialog');
    await expectNoA11yViolations(dialog.closest('[data-depth]') ?? dialog);
  });
});
