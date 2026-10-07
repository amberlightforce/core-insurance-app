import { renderHook, act, screen, within } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, useLocation } from 'react-router';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Tabs } from './Tabs';

// jsdom has no Web Animations API; React Aria's SelectionIndicator (MI-12) reads it.
if (typeof Element.prototype.getAnimations !== 'function') {
  Element.prototype.getAnimations = () => [];
}
import { adjacentTab, fitTabs, splitTabs, type TabItem } from './tabsLayout';
import { useTabSearchParam } from './useTabSearchParam';

const items: TabItem[] = [
  { id: 'summary', label: 'Σύνοψη' },
  { id: 'coverages', label: 'Καλύψεις', errorCount: 2 },
  { id: 'transactions', label: 'Συναλλαγές' },
  { id: 'charges', label: 'Χρεώσεις', disabledReason: 'Δεν έχετε δικαίωμα προβολής χρεώσεων' },
  { id: 'claims', label: 'Ζημίες', count: 3 },
  { id: 'documents', label: 'Έγγραφα' },
  { id: 'history', label: 'Ιστορικό' },
];

function renderTabs(props: Partial<Parameters<typeof Tabs>[0]> = {}) {
  return renderWithDs(
    <Tabs items={items} aria-label="Ασφαλιστήριο" {...props}>
      {(item) => (
        <div>
          <p>Περιεχόμενο {item.label}</p>
          <button type="button">Ενέργεια {item.label}</button>
        </div>
      )}
    </Tabs>,
  );
}

describe('tabs layout helpers', () => {
  it('keeps the selected tab visible by swapping it with the last visible tab', () => {
    const { visible, overflow } = splitTabs(items, 3, 'history');
    expect(visible.map((i) => i.id)).toEqual(['summary', 'coverages', 'history']);
    expect(overflow.map((i) => i.id)).toEqual(['transactions', 'charges', 'claims', 'documents']);
  });

  it('fits tabs into the available width and reserves the More button only on overflow', () => {
    expect(fitTabs([80, 80, 80], 300, 90)).toBe(3);
    expect(fitTabs([80, 80, 80, 80], 300, 90)).toBe(2);
  });

  it('finds the adjacent selectable tab, skipping disabled ones', () => {
    expect(adjacentTab(items, 'transactions', 1)?.id).toBe('claims');
    expect(adjacentTab(items, 'summary', -1)?.id).toBe('history');
  });
});

describe('Tabs', () => {
  it('renders tablist, tabs and the selected panel', () => {
    renderTabs();
    const tablist = screen.getByRole('tablist', { name: 'Ασφαλιστήριο' });
    expect(within(tablist).getAllByRole('tab')).toHaveLength(7);
    expect(screen.getByRole('tab', { name: 'Σύνοψη' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Περιεχόμενο Σύνοψη');
  });

  it('puts the error count and the count in the accessible name', () => {
    renderTabs();
    expect(screen.getByRole('tab', { name: 'Καλύψεις, 2 σφάλματα' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Ζημίες, 3' })).toBeInTheDocument();
  });

  it('activates on arrow keys by default and supports Home/End', async () => {
    const onSelectionChange = vi.fn();
    const { user } = renderTabs({ onSelectionChange });
    await user.tab();
    expect(screen.getByRole('tab', { name: 'Σύνοψη' })).toHaveFocus();
    await user.keyboard('{ArrowRight}');
    expect(onSelectionChange).toHaveBeenLastCalledWith('coverages');
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Περιεχόμενο Καλύψεις');
    await user.keyboard('{End}');
    expect(onSelectionChange).toHaveBeenLastCalledWith('history');
    await user.keyboard('{Home}');
    expect(onSelectionChange).toHaveBeenLastCalledWith('summary');
  });

  it('manual activation moves focus only; Enter selects', async () => {
    const { user } = renderTabs({ manual: true });
    await user.tab();
    await user.keyboard('{ArrowRight}');
    expect(screen.getByRole('tab', { name: /Καλύψεις/ })).toHaveFocus();
    expect(screen.getByRole('tab', { name: 'Σύνοψη' })).toHaveAttribute('aria-selected', 'true');
    await user.keyboard('{Enter}');
    expect(screen.getByRole('tab', { name: /Καλύψεις/ })).toHaveAttribute('aria-selected', 'true');
  });

  it('keeps a disabled tab with a reason focusable but not selectable', async () => {
    const { user } = renderTabs();
    const charges = screen.getByRole('tab', { name: 'Χρεώσεις' });
    expect(charges).toHaveAttribute('aria-disabled', 'true');
    expect(charges).toHaveAccessibleDescription('Δεν έχετε δικαίωμα προβολής χρεώσεων');
    await user.click(charges);
    expect(charges).toHaveAttribute('aria-selected', 'false');
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Περιεχόμενο Σύνοψη');
  });

  it('Ctrl+PgDn / Ctrl+PgUp from inside the panel switch tabs', async () => {
    const { user } = renderTabs();
    await user.click(screen.getByRole('button', { name: 'Ενέργεια Σύνοψη' }));
    await user.keyboard('{Control>}{PageDown}{/Control}');
    expect(screen.getByRole('tab', { name: /Καλύψεις/ })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: /Καλύψεις/ })).toHaveFocus();
    await user.click(screen.getByRole('button', { name: 'Ενέργεια Καλύψεις' }));
    await user.keyboard('{Control>}{PageUp}{/Control}');
    expect(screen.getByRole('tab', { name: 'Σύνοψη' })).toHaveAttribute('aria-selected', 'true');
  });

  it('moves overflowing tabs into «Περισσότερα» and keeps the selection visible', async () => {
    const { user } = renderTabs({ maxVisible: 4 });
    expect(screen.getAllByRole('tab')).toHaveLength(4);
    const more = screen.getByRole('button', { name: 'Περισσότερες καρτέλες' });
    await user.click(more);
    const menu = screen.getByRole('menu');
    expect(
      within(menu)
        .getAllByRole('menuitem')
        .map((m) => m.textContent),
    ).toEqual(['Ζημίες3', 'Έγγραφα', 'Ιστορικό']);
    await user.click(within(menu).getByRole('menuitem', { name: 'Ιστορικό' }));
    const tabs = screen.getAllByRole('tab');
    expect(tabs).toHaveLength(4);
    expect(tabs.at(-1)).toHaveAccessibleName('Ιστορικό');
    expect(tabs.at(-1)).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tabpanel')).toHaveTextContent('Περιεχόμενο Ιστορικό');
  });

  it('renders the contained variant', () => {
    const { container } = renderTabs({ variant: 'contained' });
    expect(container.querySelector('[data-variant="contained"]')).not.toBeNull();
  });

  it('works in English', async () => {
    await i18n.changeLanguage('en');
    renderTabs({ maxVisible: 3 });
    expect(screen.getByRole('tab', { name: 'Καλύψεις, 2 errors' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'More tabs' })).toHaveTextContent('More');
  });

  it('has no axe violations', async () => {
    renderTabs({ maxVisible: 5 });
    await expectNoA11yViolations();
  });
});

describe('useTabSearchParam', () => {
  it('reads and replaces ?tab=', () => {
    const wrapper = ({ children }: { children: ReactNode }) => (
      <MemoryRouter initialEntries={['/policy/1?tab=claims&x=1']}>{children}</MemoryRouter>
    );
    const { result } = renderHook(
      () => ({ param: useTabSearchParam('tab', 'summary'), location: useLocation() }),
      { wrapper },
    );
    expect(result.current.param[0]).toBe('claims');
    act(() => {
      result.current.param[1]('documents');
    });
    expect(result.current.param[0]).toBe('documents');
    expect(result.current.location.search).toBe('?tab=documents&x=1');
  });

  it('falls back when the parameter is missing', () => {
    const wrapper = ({ children }: { children: ReactNode }) => (
      <MemoryRouter initialEntries={['/policy/1']}>{children}</MemoryRouter>
    );
    const { result } = renderHook(() => useTabSearchParam('tab', 'summary'), { wrapper });
    expect(result.current[0]).toBe('summary');
  });
});
