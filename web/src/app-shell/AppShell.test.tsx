import { act, fireEvent, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { describe, expect, it, vi } from 'vitest';

import i18n, { languageStorageKey } from '../i18n';
import { expectNoA11yViolations } from '../test/axe';
import { renderWithDs } from '../test/render';
import { AppShell, type AppShellProps } from './AppShell';

const fixedNow = () => new Date('2026-10-07T11:32:00Z');

function renderShell(props: Partial<AppShellProps> = {}, path = '/underwriting') {
  const onOpenPalette = vi.fn();
  const onOpenHelp = vi.fn();
  const result = renderWithDs(
    <MemoryRouter initialEntries={[path]}>
      <AppShell
        pageTitle="Παραπομπές"
        entityName="Παράδειγμα Ασφαλιστική Α.Ε."
        onOpenPalette={onOpenPalette}
        onOpenHelp={onOpenHelp}
        now={fixedNow}
        {...props}
      >
        <h1>Οι παραπομπές μου</h1>
        <input aria-label="Φίλτρο" />
      </AppShell>
    </MemoryRouter>,
  );
  return { ...result, onOpenPalette, onOpenHelp };
}

describe('AppShell', () => {
  it('puts the skip link first and orders the landmarks banner, nav, main, footer', async () => {
    const { user } = renderShell();
    await user.tab();
    const skip = screen.getByRole('link', { name: 'Μετάβαση στο περιεχόμενο' });
    expect(skip).toHaveFocus();
    expect(skip).toHaveAttribute('href', '#main-content');

    expect(screen.getByRole('banner')).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' })).toBeInTheDocument();
    expect(screen.getByRole('main')).toHaveAttribute('id', 'main-content');
    expect(screen.getByRole('contentinfo')).toBeInTheDocument();
    const order = Array.from(document.querySelectorAll('[data-shell-region]')).map((el) =>
      el.getAttribute('data-shell-region'),
    );
    expect(order).toEqual(['banner', 'nav', 'main', 'contentinfo']);
  });

  it('marks the current module in the rail', () => {
    renderShell();
    const nav = screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' });
    const current = within(nav).getByRole('link', { name: 'Ανάληψη κινδύνου' });
    expect(current).toHaveAttribute('aria-current', 'page');
    expect(within(nav).getByRole('link', { name: 'Αρχική' })).not.toHaveAttribute('aria-current');
  });

  it('opens the palette with Ctrl+K from anywhere, including inside inputs', () => {
    const { onOpenPalette } = renderShell();
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Φίλτρο' }), { key: 'k', ctrlKey: true });
    expect(onOpenPalette).toHaveBeenCalledTimes(1);
  });

  it('opens help with ? but not while typing, and with the help button', async () => {
    const { onOpenHelp, user } = renderShell();
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Φίλτρο' }), { key: '?' });
    expect(onOpenHelp).not.toHaveBeenCalled();
    fireEvent.keyDown(document.body, { key: '?' });
    expect(onOpenHelp).toHaveBeenCalledTimes(1);
    await user.click(screen.getByRole('button', { name: 'Βοήθεια και συντομεύσεις' }));
    expect(onOpenHelp).toHaveBeenCalledTimes(2);
  });

  it('ignores single-key shortcuts when the user switched them off', () => {
    const onOpenHelp = vi.fn();
    renderWithDs(
      <MemoryRouter>
        <AppShell
          pageTitle="Αρχική"
          entityName="Α.Ε."
          onOpenPalette={vi.fn()}
          onOpenHelp={onOpenHelp}
          now={fixedNow}
        >
          <p>περιεχόμενο</p>
        </AppShell>
      </MemoryRouter>,
      { preferences: { singleKeyShortcuts: false } },
    );
    fireEvent.keyDown(document.body, { key: '?' });
    expect(onOpenHelp).not.toHaveBeenCalled();
  });

  it('cycles landmark regions with F6 and Shift+F6', () => {
    renderShell();
    fireEvent.keyDown(document.body, { key: 'F6' });
    expect(screen.getByRole('banner')).toHaveFocus();
    fireEvent.keyDown(document.body, { key: 'F6' });
    expect(screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' })).toHaveFocus();
    fireEvent.keyDown(document.body, { key: 'F6' });
    expect(screen.getByRole('main')).toHaveFocus();
    fireEvent.keyDown(document.body, { key: 'F6' });
    expect(screen.getByRole('contentinfo')).toHaveFocus();
    fireEvent.keyDown(document.body, { key: 'F6', shiftKey: true });
    expect(screen.getByRole('main')).toHaveFocus();
  });

  it('toggles the rail with [ and remembers the choice', () => {
    renderShell();
    const nav = screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' });
    expect(nav).not.toHaveAttribute('data-expanded');
    fireEvent.keyDown(document.body, { key: '[' });
    expect(nav).toHaveAttribute('data-expanded', 'true');
  });

  it('switches the language, updates <html lang> and persists it (R-101)', async () => {
    const { user } = renderShell();
    expect(document.documentElement.lang).toBe('el');
    await user.click(screen.getByRole('radio', { name: 'English' }));
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem(languageStorageKey)).toBe('en');
    expect(screen.getByRole('navigation', { name: 'Main navigation' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Skip to content' })).toBeInTheDocument();
    await act(() => i18n.changeLanguage('el'));
  });

  it('sets the document title and shows the environment ribbon in capitals without tonos', () => {
    renderShell({ environment: { kind: 'uat', name: 'UAT', shiftedDate: '15/01/2027' } });
    expect(document.title).toBe('[UAT] Παραπομπές · Core Insurance');
    const ribbon = screen.getByRole('note');
    expect(ribbon).toHaveTextContent('ΠΕΡΙΒΑΛΛΟΝ ΔΟΚΙΜΩΝ · UAT · ΤΑ ΔΕΔΟΜΕΝΑ ΔΕΝ ΕΙΝΑΙ ΠΡΑΓΜΑΤΙΚΑ');
    expect(ribbon).toHaveTextContent('Ημερομηνία συστήματος: 15/01/2027');
  });

  it('shows Athens time, connectivity and AI state in the status bar', () => {
    renderShell({ aiEnabled: false, backgroundJobs: 2 });
    const status = screen.getByRole('contentinfo');
    expect(status).toHaveTextContent('07/10/2026 · 14:32 EEST');
    expect(status).toHaveTextContent('Συνδεδεμένο');
    expect(status).toHaveTextContent('ΤΝ απενεργοποιημένη');
    expect(status).toHaveTextContent('2 εργασίες στο παρασκήνιο');
  });

  it('renders work views and the context panel as labelled landmarks', () => {
    renderShell({ workViews: <p>Προβολές</p>, contextPanel: <p>Δραστηριότητα</p> });
    expect(screen.getByRole('navigation', { name: 'Προβολές εργασίας' })).toBeInTheDocument();
    expect(screen.getByRole('complementary', { name: 'Πλαίσιο' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderShell({ workViews: <p>Προβολές</p>, contextPanel: <p>Δραστηριότητα</p> });
    await expectNoA11yViolations(container);
  });
});
