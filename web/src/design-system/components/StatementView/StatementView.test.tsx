import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { StatementView, type StatementLine } from './index';

const lines: StatementLine[] = [
  {
    id: '1',
    description: 'Προμήθεια Σεπτεμβρίου',
    amount: 1250,
    date: new Date(2026, 8, 30),
    reference: 'ΠΡΜ-0912',
  },
  {
    id: '2',
    description: 'Αντιλογισμός ακύρωσης',
    amount: -210.5,
    date: new Date(2026, 9, 2),
    reference: 'ΑΚΥ-0044',
  },
  { id: '3', description: 'Σύνολο περιόδου', amount: 1039.5, kind: 'subtotal' },
];

/** Normalises NBSP/narrow NBSP so expectations can use plain spaces. */
function text(el: Element | undefined): string {
  return (el?.textContent ?? '').replace(/\s/gu, ' ');
}

describe('StatementView', () => {
  it('shows the headline balance with its direction label', () => {
    renderWithDs(
      <StatementView
        title="Προμήθειες"
        balance={-1039.5}
        direction="payable"
        lines={lines}
        adverseRule="negative"
      />,
    );
    const region = screen.getByRole('region', { name: 'Προμήθειες' });
    expect(text(region)).toContain('1.039,50 €');
    expect(region).toHaveTextContent('Οφειλή από εμάς');
  });

  it('lists line items with date, reference and right-aligned amounts using U+2212', () => {
    renderWithDs(
      <StatementView
        balance={1039.5}
        direction="receivable"
        lines={lines}
        adverseRule="negative"
      />,
    );
    const list = screen.getByRole('list', { name: 'Κινήσεις' });
    const items = within(list).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(text(items[0])).toContain('30/09/2026');
    expect(text(items[0])).toContain('ΠΡΜ-0912');
    expect(text(items[1])).toContain('−210,50 €');
    expect(items[2]).toHaveAttribute('data-kind', 'subtotal');
  });

  it('marks adverse movements from the statement-type rule, not the sign', () => {
    const { rerender } = renderWithDs(
      <StatementView balance={0} direction="settled" lines={lines} adverseRule="negative" />,
    );
    let items = screen.getAllByRole('listitem');
    expect(items[1]).toHaveAttribute('data-adverse', 'true');
    expect(items[1]).toHaveTextContent('(δυσμενής κίνηση)');
    expect(items[0]).not.toHaveAttribute('data-adverse');

    rerender(
      <StatementView balance={0} direction="settled" lines={lines} adverseRule="positive" />,
    );
    items = screen.getAllByRole('listitem');
    expect(items[0]).toHaveAttribute('data-adverse', 'true');
    expect(items[1]).not.toHaveAttribute('data-adverse');

    rerender(
      <StatementView
        balance={0}
        direction="settled"
        lines={[{ id: 'x', description: 'Συμψηφισμός', amount: -5, isAdverse: false }]}
        adverseRule="negative"
      />,
    );
    expect(screen.getByRole('listitem')).not.toHaveAttribute('data-adverse');
    expect(screen.getByText('Εξοφλημένο')).toBeInTheDocument();
  });

  it('computes an optional running balance from the opening balance, skipping subtotals', () => {
    renderWithDs(
      <StatementView
        balance={1539.5}
        direction="receivable"
        lines={lines}
        adverseRule="negative"
        showRunningBalance
        openingBalance={500}
      />,
    );
    const items = screen.getAllByRole('listitem');
    expect(text(items[0])).toContain('Υπόλοιπο1.750,00 €');
    expect(text(items[1])).toContain('Υπόλοιπο1.539,50 €');
  });

  it('opens linked rows with Enter', async () => {
    const onPress = vi.fn();
    const { user } = renderWithDs(
      <StatementView
        balance={10}
        direction="receivable"
        lines={[{ id: 'a', description: 'Πληρωμή 0001', amount: 10, onPress }]}
        adverseRule="none"
      />,
    );
    await user.tab();
    expect(screen.getByRole('link', { name: 'Πληρωμή 0001' })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(onPress).toHaveBeenCalledOnce();
  });

  it('renders loading, empty and error states', () => {
    const { rerender, container } = renderWithDs(
      <StatementView
        balance={0}
        direction="settled"
        lines={[]}
        adverseRule="negative"
        state="loading"
      />,
    );
    expect(container.querySelector('[aria-busy="true"]')).not.toBeNull();
    rerender(<StatementView balance={0} direction="settled" lines={[]} adverseRule="negative" />);
    expect(screen.getByText('Καμία κίνηση στην περίοδο')).toBeInTheDocument();
    rerender(
      <StatementView
        balance={0}
        direction="settled"
        lines={[]}
        adverseRule="negative"
        state="error"
        onRetry={() => undefined}
      />,
    );
    expect(screen.getByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Επανάληψη' })).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <StatementView balance={5} direction="receivable" lines={[]} adverseRule="none" />,
    );
    expect(screen.getByText('Owed to us')).toBeInTheDocument();
    expect(screen.getByText('No movements in this period')).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <StatementView
        title="Κινήσεις λογαριασμού"
        balance={1039.5}
        direction="receivable"
        lines={[...lines, { id: '4', description: 'Πληρωμή', amount: 5, href: '/p/4' }]}
        adverseRule="negative"
        showRunningBalance
      />,
    );
    await expectNoA11yViolations(container);
  });
});
