import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Breadcrumbs } from './Breadcrumbs';
import { collapseBreadcrumbs, type BreadcrumbItem } from './collapse';

const short: BreadcrumbItem[] = [
  { id: 'home', label: 'Αρχική', href: '/' },
  { id: 'policies', label: 'Ασφαλιστήρια', href: '/policies' },
  { id: 'policy', label: 'ΑΣΦ-2026-000412' },
];

const long: BreadcrumbItem[] = [
  { id: 'home', label: 'Αρχική' },
  { id: 'claims', label: 'Ζημίες' },
  { id: 'claim', label: 'ΖΗΜ-2026-001877' },
  { id: 'exposure', label: 'Έκθεση 02' },
  { id: 'payments', label: 'Πληρωμές' },
  { id: 'payment', label: 'Πληρωμή 3' },
];

describe('collapseBreadcrumbs', () => {
  it('keeps the first and last two when there are more than four', () => {
    const result = collapseBreadcrumbs(long);
    expect(result.head.map((i) => i.id)).toEqual(['home']);
    expect(result.collapsed.map((i) => i.id)).toEqual(['claims', 'claim', 'exposure']);
    expect(result.tail.map((i) => i.id)).toEqual(['payments', 'payment']);
    expect(collapseBreadcrumbs(short).collapsed).toEqual([]);
  });
});

describe('Breadcrumbs', () => {
  it('is a labelled nav with an ordered list and the current page', () => {
    renderWithDs(<Breadcrumbs items={short} />);
    const nav = screen.getByRole('navigation', { name: 'Διαδρομή πλοήγησης' });
    expect(within(nav).getByRole('list').tagName).toBe('OL');
    expect(within(nav).getAllByRole('listitem')).toHaveLength(3);
    expect(screen.getByRole('link', { name: 'Ασφαλιστήρια' })).toHaveAttribute('href', '/policies');
    expect(screen.getByText('ΑΣΦ-2026-000412')).toHaveAttribute('aria-current', 'page');
  });

  it('collapses the middle into a ⋯ menu and navigates from it', async () => {
    const onAction = vi.fn();
    const { user } = renderWithDs(<Breadcrumbs items={long} onAction={onAction} />);
    expect(screen.getAllByRole('listitem')).toHaveLength(4);
    const more = screen.getByRole('button', { name: '3 ακόμη επίπεδα' });
    await user.click(more);
    const menu = screen.getByRole('menu');
    expect(within(menu).getAllByRole('menuitem')).toHaveLength(3);
    await user.click(within(menu).getByRole('menuitem', { name: 'ΖΗΜ-2026-001877' }));
    expect(onAction).toHaveBeenCalledWith('claim');
    // Keyboard: open with Enter, move with arrows, activate with Enter.
    more.focus();
    await user.keyboard('{Enter}');
    await user.keyboard('{ArrowDown}{Enter}');
    expect(onAction).toHaveBeenCalledTimes(2);
  });

  it('activates links by keyboard', async () => {
    const onAction = vi.fn();
    const { user } = renderWithDs(<Breadcrumbs items={long.slice(0, 3)} onAction={onAction} />);
    await user.tab();
    expect(screen.getByRole('link', { name: 'Αρχική' })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(onAction).toHaveBeenCalledWith('home');
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Breadcrumbs items={long} />);
    expect(screen.getByRole('navigation', { name: 'Breadcrumb' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '3 more levels' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderWithDs(<Breadcrumbs items={long} />);
    await expectNoA11yViolations();
  });
});
