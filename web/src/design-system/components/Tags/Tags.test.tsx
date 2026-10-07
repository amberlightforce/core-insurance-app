import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { chipText, type FilterChipItem } from './chipText';
import { FilterChips } from './Tags';

const items: FilterChipItem[] = [
  { id: 'status', field: 'Κατάσταση', values: ['Σε ισχύ', 'Σε εκκρεμή ακύρωση'] },
  { id: 'branch', field: 'Κλάδος', values: ['Αυτοκίνητο'] },
  { id: 'entity', field: 'Νομική οντότητα', values: ['Ελλάδα'], state: 'locked' },
  { id: 'old', field: 'Παραγωγός', values: ['10233'], state: 'invalid' },
  { id: 'ro', field: 'Περίοδος', values: ['2026'], state: 'readOnly' },
];

describe('chipText', () => {
  it('truncates values after 32 characters', () => {
    const text = chipText({ id: 'x', values: ['Σε ισχύ', 'Σε εκκρεμή ακύρωση', 'Ληξιπρόθεσμο'] });
    expect(text.truncated).toBe(true);
    expect(text.shown.endsWith('…')).toBe(true);
    expect(Array.from(text.shown).length).toBeLessThanOrEqual(33);
    expect(chipText({ id: 'y', values: ['Σε ισχύ'] }).truncated).toBe(false);
  });
});

describe('FilterChips', () => {
  it('renders a labelled grid of chips with the field and values', () => {
    renderWithDs(<FilterChips items={items} onRemove={vi.fn()} />);
    const grid = screen.getByRole('grid', { name: 'Ενεργά φίλτρα' });
    expect(within(grid).getAllByRole('row')).toHaveLength(5);
    expect(
      within(grid).getByRole('row', { name: /Κατάσταση: Σε ισχύ, Σε εκκρεμή ακύρωση/ }),
    ).toBeInTheDocument();
  });

  it('offers × only on removable chips', () => {
    renderWithDs(<FilterChips items={items} onRemove={vi.fn()} />);
    expect(
      screen.getByRole('button', {
        name: 'Αφαίρεση φίλτρου Κατάσταση: Σε ισχύ, Σε εκκρεμή ακύρωση',
      }),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Νομική οντότητα/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Περίοδος/ })).not.toBeInTheDocument();
  });

  it('removes with Delete and Backspace, never a locked chip', async () => {
    const onRemove = vi.fn();
    const { user } = renderWithDs(<FilterChips items={items} onRemove={onRemove} />);
    await user.tab();
    expect(screen.getByRole('row', { name: /Κατάσταση/ })).toHaveFocus();
    await user.keyboard('{Delete}');
    expect(onRemove).toHaveBeenLastCalledWith('status');
    await user.keyboard('{ArrowRight}{Backspace}');
    expect(onRemove).toHaveBeenLastCalledWith('branch');
    await user.keyboard('{ArrowRight}{Delete}');
    expect(onRemove).toHaveBeenCalledTimes(2);
  });

  it('removes with the × button', async () => {
    const onRemove = vi.fn();
    const { user } = renderWithDs(<FilterChips items={items} onRemove={onRemove} />);
    await user.click(screen.getByRole('button', { name: /Αφαίρεση φίλτρου Κλάδος/ }));
    expect(onRemove).toHaveBeenCalledWith('branch');
  });

  it('Enter and click edit the filter', async () => {
    const onEdit = vi.fn();
    const { user } = renderWithDs(<FilterChips items={items} onEdit={onEdit} onRemove={vi.fn()} />);
    await user.tab();
    await user.keyboard('{Enter}');
    expect(onEdit).toHaveBeenLastCalledWith('status');
    await user.click(screen.getByText('Κλάδος: Αυτοκίνητο'));
    expect(onEdit).toHaveBeenLastCalledWith('branch');
  });

  it('marks locked, invalid and read-only chips', () => {
    renderWithDs(<FilterChips items={items} onRemove={vi.fn()} />);
    const locked = screen.getByRole('row', { name: /Νομική οντότητα/ });
    expect(locked).toHaveAttribute('data-state', 'locked');
    expect(locked).toHaveAccessibleName(/Φίλτρο δικαιωμάτων/);
    const invalid = screen.getByRole('row', { name: /Παραγωγός/ });
    expect(invalid).toHaveAttribute('data-state', 'invalid');
    expect(invalid).toHaveAccessibleName(/Το πεδίο δεν υπάρχει πλέον/);
    expect(screen.getByRole('row', { name: /Περίοδος/ })).toHaveAttribute('data-state', 'readOnly');
  });

  it('keeps the full text of a truncated chip in its name and shows a tooltip on focus', async () => {
    const long: FilterChipItem[] = [
      { id: 's', field: 'Κατάσταση', values: ['Σε ισχύ', 'Σε εκκρεμή ακύρωση', 'Ληξιπρόθεσμο'] },
    ];
    const { user } = renderWithDs(<FilterChips items={long} />);
    const row = screen.getByRole('row');
    expect(row).toHaveAccessibleName(/Σε ισχύ, Σε εκκρεμή ακύρωση, Ληξιπρόθεσμο/);
    await user.tab();
    expect(await screen.findByRole('tooltip')).toHaveTextContent(
      'Κατάσταση: Σε ισχύ, Σε εκκρεμή ακύρωση, Ληξιπρόθεσμο',
    );
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<FilterChips items={items.slice(3, 4)} onRemove={vi.fn()} />);
    expect(screen.getByRole('grid', { name: 'Active filters' })).toBeInTheDocument();
    expect(screen.getByRole('row')).toHaveAccessibleName(/This field no longer exists/);
  });

  it('has no axe violations', async () => {
    renderWithDs(<FilterChips items={items} onRemove={vi.fn()} onEdit={vi.fn()} />);
    await expectNoA11yViolations();
  });
});
