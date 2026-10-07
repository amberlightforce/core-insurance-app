import { renderHook, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FilePlus, Shield } from 'lucide-react';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { clearAnnouncements } from '../../a11y/announce';
import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { CommandPalette, type CommandPaletteProps } from './CommandPalette';
import { buildSections, nextScope, parsePrefix, type PaletteItem } from './paletteQuery';
import { useCommandPaletteShortcut } from './useCommandPaletteShortcut';

afterEach(() => {
  clearAnnouncements();
});

function makeItems(onAction = vi.fn()): PaletteItem[] {
  return [
    {
      id: 'new-quote',
      group: 'actions',
      title: 'Νέα προσφορά',
      icon: FilePlus,
      shortcut: 'Q',
      onAction,
    },
    { id: 'new-claim', group: 'actions', title: 'Νέα αναγγελία ζημίας', shortcut: 'N Z', onAction },
    { id: 'go-policies', group: 'navigation', title: 'Ασφαλιστήρια', shortcut: 'G P', onAction },
    {
      id: 'pol-4471',
      group: 'records',
      kind: 'policy',
      code: 'ΑΣΦ-2026-004471',
      title: 'Μαρία Παπαδοπούλου',
      subtitle: 'ΑΣΦ-2026-004471 · Σε ισχύ · Αυτοκίνητο ΙΧ',
      icon: Shield,
      onAction,
    },
    {
      id: 'clm-0091',
      group: 'records',
      kind: 'claim',
      code: 'ΖΗΜ-2026-000091',
      title: 'Κώστας Νικολάου',
      subtitle: 'ΖΗΜ-2026-000091 · Ανοιχτή',
      onAction,
    },
    { id: 'recent-1', group: 'recent', title: 'Ελένη Γεωργίου', subtitle: 'ΠΡΣ-1182', onAction },
  ];
}

function Harness(props: Partial<CommandPaletteProps> & { items?: PaletteItem[] }) {
  const [open, setOpen] = useState(true);
  return (
    <CommandPalette
      isOpen={open}
      onOpenChange={setOpen}
      items={props.items ?? makeItems()}
      {...props}
    />
  );
}

describe('palette query helpers', () => {
  it('parses scope prefixes in Greek and English, accent-insensitively', () => {
    expect(parsePrefix('ασφ: 4471')).toEqual({ scope: 'policy', rest: '4471' });
    expect(parsePrefix('άσφ:Παπ')).toEqual({ scope: 'policy', rest: 'Παπ' });
    expect(parsePrefix('POL:abc')).toEqual({ scope: 'policy', rest: 'abc' });
    expect(parsePrefix('ζημ:')).toEqual({ scope: 'claim', rest: '' });
    expect(parsePrefix('cus: x')).toEqual({ scope: 'customer', rest: 'x' });
    expect(parsePrefix('>ανάθεση')).toEqual({ scope: 'actions', rest: 'ανάθεση' });
    expect(parsePrefix('#4471')).toEqual({ scope: 'id', rest: '4471' });
    expect(parsePrefix('ανανέωση')).toEqual({ scope: null, rest: 'ανανέωση' });
  });

  it('cycles the type filter', () => {
    expect(nextScope(null)).toBe('policy');
    expect(nextScope('policy')).toBe('claim');
    expect(nextScope('actions')).toBeNull();
  });

  it('shows recents for an empty query and matches Greeklish', () => {
    const items = makeItems();
    expect(buildSections(items, [], '', null).map((s) => s.group)).toEqual(['recent']);
    const sections = buildSections(items, [], 'papad', null);
    expect(sections).toHaveLength(1);
    expect(sections[0]?.hits[0]?.item.id).toBe('pol-4471');
    expect(sections[0]?.hits[0]?.ranges).toEqual([{ start: 6, end: 11 }]);
    expect(buildSections(items, [], '4471', 'id')[0]?.hits[0]?.item.id).toBe('pol-4471');
    expect(buildSections(items, [], '', 'actions')[0]?.hits).toHaveLength(2);
  });
});

describe('CommandPalette', () => {
  it('opens as a labelled dialog with the input focused and recents shown', async () => {
    renderWithDs(<Harness />);
    const dialog = await screen.findByRole('dialog', { name: 'Παλέτα εντολών' });
    const input = within(dialog).getByRole('searchbox');
    await waitFor(() => {
      expect(input).toHaveFocus();
    });
    expect(within(dialog).getByText('ΠΡΟΣΦΑΤΑ')).toBeInTheDocument();
    expect(within(dialog).getByRole('menuitem', { name: /Ελένη Γεωργίου/ })).toBeInTheDocument();
  });

  it('filters accent-insensitively, highlights, navigates with arrows and runs with Enter', async () => {
    const onAction = vi.fn();
    const { user } = renderWithDs(<Harness items={makeItems(onAction)} />);
    const input = await screen.findByRole('searchbox');
    await user.type(input, 'νεα');
    const menu = screen.getByRole('menu');
    expect(within(menu).getByText('ΕΝΕΡΓΕΙΕΣ')).toBeInTheDocument();
    const items = within(menu).getAllByRole('menuitem');
    expect(items).toHaveLength(2);
    expect(items[0]?.querySelector('mark')).toHaveTextContent('Νέα');
    await waitFor(() => {
      expect(input).toHaveAttribute('aria-activedescendant', items[0]?.id);
    });
    await user.keyboard('{ArrowDown}');
    await waitFor(() => {
      expect(input).toHaveAttribute('aria-activedescendant', items[1]?.id);
    });
    await user.keyboard('{Enter}');
    expect(onAction).toHaveBeenCalledTimes(1);
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('turns a prefix into a scope chip that Backspace removes, and Tab cycles it', async () => {
    const { user } = renderWithDs(<Harness />);
    const input = await screen.findByRole('searchbox');
    await user.type(input, 'ζημ:');
    expect(input).toHaveValue('');
    expect(screen.getByRole('button', { name: 'Αφαίρεση φίλτρου' })).toHaveTextContent(
      'Σε: Ζημίες',
    );
    expect(screen.getAllByRole('menuitem')).toHaveLength(1);
    await user.keyboard('{Backspace}');
    expect(screen.queryByRole('button', { name: 'Αφαίρεση φίλτρου' })).not.toBeInTheDocument();
    await user.keyboard('{Tab}');
    expect(screen.getByRole('button', { name: 'Αφαίρεση φίλτρου' })).toHaveTextContent(
      'Σε: Ασφαλιστήρια',
    );
    expect(input).toHaveFocus();
  });

  it('offers full-text search when nothing matches', async () => {
    const onFullTextSearch = vi.fn();
    const { user } = renderWithDs(<Harness onFullTextSearch={onFullTextSearch} />);
    const input = await screen.findByRole('searchbox');
    await user.type(input, 'ξυλοκάρβουνο');
    expect(
      screen.getByRole('menuitem', { name: 'Καμία αντιστοιχία · Αναζήτηση σε πλήρες κείμενο (↵)' }),
    ).toBeInTheDocument();
    await user.keyboard('{Enter}');
    expect(onFullTextSearch).toHaveBeenCalledWith('ξυλοκάρβουνο');
  });

  it('merges remote records and keeps local actions when search fails', async () => {
    const remote: PaletteItem = {
      id: 'cus-77',
      group: 'records',
      kind: 'customer',
      title: 'Νίκος Δημητρίου',
      onAction: vi.fn(),
    };
    const onSearch = vi.fn((query: string) =>
      query.startsWith('νικ') ? Promise.resolve([remote]) : Promise.reject(new Error('offline')),
    );
    const { user } = renderWithDs(<Harness onSearch={onSearch} />);
    const input = await screen.findByRole('searchbox');
    await user.type(input, 'νικ');
    expect(await screen.findByRole('menuitem', { name: /Νίκος Δημητρίου/ })).toBeInTheDocument();
    expect(onSearch).toHaveBeenLastCalledWith('νικ', null);

    await user.clear(input);
    await user.type(input, 'νεα');
    expect(await screen.findByText('Η αναζήτηση δεν είναι διαθέσιμη')).toBeInTheDocument();
    expect(screen.getAllByRole('menuitem', { name: /Νέα/ }).length).toBeGreaterThan(0);
  });

  it('opens the highlighted item in a new tab with Ctrl+Enter', async () => {
    const onOpenInNewTab = vi.fn();
    const { user } = renderWithDs(<Harness onOpenInNewTab={onOpenInNewTab} />);
    const input = await screen.findByRole('searchbox');
    await user.type(input, 'papad');
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(onOpenInNewTab).toHaveBeenCalledWith(expect.objectContaining({ id: 'pol-4471' }));
  });

  it('announces the result count politely and closes with Esc', async () => {
    const { user } = renderWithDs(<Harness />);
    const input = await screen.findByRole('searchbox');
    await user.type(input, 'νεα');
    await waitFor(() => {
      expect(document.getElementById('ds-live-polite')).toHaveTextContent('2 αποτελέσματα');
    });
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Harness />);
    expect(await screen.findByRole('dialog', { name: 'Command palette' })).toBeInTheDocument();
    expect(screen.getByText('RECENT')).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { user } = renderWithDs(<Harness />);
    const input = await screen.findByRole('searchbox');
    await user.type(input, 'νεα');
    const dialog = screen.getByRole('dialog');
    await expectNoA11yViolations(dialog.closest('[data-material]') ?? dialog);
  });
});

describe('useCommandPaletteShortcut', () => {
  it('toggles with Ctrl+K even inside a text field', async () => {
    const onToggle = vi.fn();
    renderHook(() => {
      useCommandPaletteShortcut(onToggle);
    });
    const input = document.createElement('input');
    document.body.appendChild(input);
    input.focus();
    const user = userEvent.setup();
    await user.keyboard('{Control>}k{/Control}');
    expect(onToggle).toHaveBeenCalledTimes(1);
    input.remove();
  });
});
