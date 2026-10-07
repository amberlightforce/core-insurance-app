import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import {
  formatCompactMoney,
  formatDelta,
  formatMoney,
  formatNumber,
  formatPercent,
} from './format';
import { HeroMetric, KpiTile } from './index';

/** Matches an accessible name, treating NBSP and narrow NBSP as spaces. */
function nameIs(expected: string) {
  return (name: string) => name.replace(/\s/gu, ' ') === expected;
}

describe('KPI formatting (Part 3 §6.4)', () => {
  it('abbreviates money by magnitude in el-GR', () => {
    expect(formatCompactMoney(8420, 'el-GR')).toBe('8.420\u00A0€');
    expect(formatCompactMoney(48230, 'el-GR')).toBe('48,2\u00A0χιλ.\u00A0€');
    expect(formatCompactMoney(482300, 'el-GR')).toBe('482\u00A0χιλ.\u00A0€');
    expect(formatCompactMoney(1284390, 'el-GR')).toBe('1,28\u00A0εκ.\u00A0€');
    expect(formatCompactMoney(1284390, 'en-GB')).toBe('€1.28M');
  });

  it('uses U+2212 for negatives and always groups', () => {
    expect(formatMoney(-1234.5, 'el-GR')).toBe('\u22121.234,50\u00A0€');
    expect(formatNumber(4812, 'el-GR')).toBe('4.812');
  });

  it('shows the sign on deltas and a narrow space before % in el-GR', () => {
    expect(formatDelta(4.2, 'percent', 'el-GR')).toBe('+4,2\u202F%');
    expect(formatDelta(-1.8, 'percent', 'el-GR')).toBe('\u22121,8\u202F%');
    expect(formatDelta(2.1, 'points', 'el-GR')).toBe('+2,1\u00A0μ.');
    expect(formatPercent(68.4, 'en-GB')).toBe('68.4%');
  });
});

describe('KpiTile', () => {
  it('names the tile with value, delta and period, and abbreviates the visible value', () => {
    renderWithDs(
      <KpiTile
        label="Ασφάλιστρα"
        value={1284390}
        format="money"
        delta={{ value: 4.2, basis: 'vs προηγ. μήνα' }}
        period="Οκτώβριος 2026"
        asOf="14:32"
      />,
    );
    const tile = screen.getByRole('group', {
      name: nameIs(
        'Ασφάλιστρα: 1.284.390,00 €, +4,2 % vs προηγ. μήνα (ευνοϊκή μεταβολή), Οκτώβριος 2026',
      ),
    });
    expect(tile).toHaveTextContent('1,28 εκ. €');
    expect(tile).toHaveTextContent('Ενημέρωση 14:32');
  });

  it('colours the delta by per-KPI favourability, not by sign', () => {
    const { rerender } = renderWithDs(
      <KpiTile
        label="Δείκτης ζημιών"
        value={68.4}
        format="percent"
        higherIsBetter={false}
        delta={{ value: 2.1, unit: 'points', basis: 'vs Σεπ' }}
      />,
    );
    expect(screen.getByText(/vs Σεπ/).closest('[data-tone]')).toHaveAttribute(
      'data-tone',
      'danger',
    );
    rerender(
      <KpiTile
        label="Δείκτης ζημιών"
        value={68.4}
        format="percent"
        higherIsBetter={false}
        delta={{ value: -2.1, unit: 'points', basis: 'vs Σεπ' }}
      />,
    );
    expect(screen.getByText(/vs Σεπ/).closest('[data-tone]')).toHaveAttribute(
      'data-tone',
      'success',
    );
  });

  it('shows the full value in a tooltip on focus', async () => {
    const { user } = renderWithDs(<KpiTile label="Ασφάλιστρα" value={1284390} format="money" />);
    await user.tab();
    expect(await screen.findByRole('tooltip')).toHaveTextContent('1.284.390,00 €');
  });

  it('opens the definition popover with formula and source', async () => {
    const { user } = renderWithDs(
      <KpiTile
        label="Νέα παραγωγή"
        value={412}
        format="count"
        definition={{
          formula: 'Πλήθος δεσμεύσεων',
          source: 'mart.pol_new_business',
          asOf: '06/10/2026',
        }}
      />,
    );
    const trigger = screen.getByRole('button', { name: 'Ορισμός: Νέα παραγωγή' });
    await user.click(trigger);
    const dialog = await screen.findByRole('dialog', { name: 'Ορισμός: Νέα παραγωγή' });
    expect(dialog).toHaveTextContent('Πλήθος δεσμεύσεων');
    expect(dialog).toHaveTextContent('mart.pol_new_business');
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).toBeNull();
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('draws a sparkline as an image; money sparklines never animate', () => {
    const { rerender } = renderWithDs(
      <KpiTile
        label="Ασφάλιστρα"
        value={1280000}
        format="money"
        trend={[1020000, 1100000, 1280000]}
      />,
    );
    const img = screen.getByRole('img', { name: nameIs('Τάση: από 1,02 εκ. € σε 1,28 εκ. €') });
    expect(img).not.toHaveAttribute('data-draw');
    expect(img).toHaveAttribute('data-tone', 'positive');
    rerender(
      <KpiTile
        label="Ανοιχτές ζημίες"
        value={120}
        format="count"
        higherIsBetter={false}
        trend={[100, 120]}
      />,
    );
    const counts = screen.getByRole('img');
    expect(counts).toHaveAttribute('data-draw', 'true');
    expect(counts).toHaveAttribute('data-tone', 'adverse');
  });

  it('shows the stale as-of caption', () => {
    renderWithDs(<KpiTile label="Α" value={1} asOf="14:32" isStale />);
    expect(screen.getByText(/Ενημέρωση 14:32/).closest('[data-stale]')).not.toBeNull();
    expect(screen.getByText('(τα δεδομένα δεν είναι ενημερωμένα)')).toBeInTheDocument();
  });

  it('renders loading and error states with retry', async () => {
    const onRetry = vi.fn();
    const { rerender, user } = renderWithDs(<KpiTile label="Α" value={null} state="loading" />);
    expect(screen.getByRole('group', { name: 'Α' })).toHaveAttribute('aria-busy', 'true');
    rerender(<KpiTile label="Α" value={null} state="error" onRetry={onRetry} />);
    expect(screen.getByText('Δεν είναι διαθέσιμο')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Επανάληψη' }));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it('links the whole tile to the report', async () => {
    const onPress = vi.fn();
    const { user } = renderWithDs(
      <KpiTile label="Ακυρώσεις" value={12} format="count" onPress={onPress} />,
    );
    const link = screen.getByRole('link', { name: 'Προβολή αναφοράς: Ακυρώσεις: 12' });
    await user.click(link);
    expect(onPress).toHaveBeenCalledOnce();
  });

  it('renders in English with en-GB formats when the region format is en-GB', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<KpiTile label="Premium" value={1284390} format="money" />, {
      preferences: { regionFormat: 'en-GB' },
    });
    expect(screen.getByRole('group', { name: nameIs('Premium: €1,284,390.00') })).toHaveTextContent(
      '€1.28M',
    );
  });
});

describe('HeroMetric', () => {
  it('shows the full value, comparison and a 64 px trend', () => {
    renderWithDs(
      <HeroMetric
        label="Ασφάλιστρα νέας παραγωγής Οκτ."
        value={1284390}
        fullValue="1.284.390 €"
        comparison="104 % του πλάνου"
        trend={[900000, 1284390]}
      />,
    );
    expect(
      screen.getByRole('group', {
        name: nameIs('Ασφάλιστρα νέας παραγωγής Οκτ.: 1.284.390 €, 104 % του πλάνου'),
      }),
    ).toHaveTextContent('1.284.390 €');
    expect(screen.getByRole('img')).not.toHaveAttribute('data-draw');
  });
});

describe('KPI accessibility', () => {
  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <KpiTile
          label="Ασφάλιστρα"
          value={1284390}
          format="money"
          delta={{ value: -1.8, basis: 'vs πλάνο' }}
          trend={[1, 3, 2]}
          definition={{ formula: 'Σ', source: 'mart' }}
          href="/reports/gwp"
          asOf="14:32"
        />
        <KpiTile label="Σφάλμα" value={null} state="error" onRetry={() => undefined} />
        <HeroMetric label="Ήρωας" value={10} format="count" comparison="vs Σεπ" />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
