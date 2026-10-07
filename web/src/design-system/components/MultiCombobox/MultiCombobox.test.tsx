import { screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import type { ComboboxOption } from '../Combobox';
import { MultiCombobox } from './MultiCombobox';

const covers: ComboboxOption[] = [
  { id: 'fire', label: 'Πυρκαγιά', caption: 'Βασική κάλυψη' },
  { id: 'theft', label: 'Κλοπή', caption: 'Προαιρετική' },
  { id: 'quake', label: 'Σεισμός', caption: 'Προαιρετική' },
  { id: 'flood', label: 'Πλημμύρα', caption: 'Προαιρετική' },
  { id: 'glass', label: 'Θραύση κρυστάλλων', caption: 'Προαιρετική' },
];

function input() {
  return screen.getByRole('combobox', { name: /Καλύψεις/ });
}

function chips() {
  const grid = screen.queryByRole('grid', { name: /Επιλεγμένα/, hidden: true });
  return grid ? within(grid).getAllByRole('row', { hidden: true }) : [];
}

describe('MultiCombobox', () => {
  it('adds chips from the list and keeps the list open', async () => {
    const onSelectionChange = vi.fn();
    const { user } = renderWithDs(
      <MultiCombobox label="Καλύψεις" items={covers} onSelectionChange={onSelectionChange} />,
    );
    await user.type(input(), 'seism');
    await user.click(screen.getByRole('option', { name: 'Σεισμός' }));
    expect(onSelectionChange).toHaveBeenLastCalledWith(['quake'], [covers[2]]);
    expect(chips().map((c) => c.textContent)).toEqual(['Σεισμός']);
    expect(input()).toHaveValue('');
    await user.type(input(), 'pl');
    await user.keyboard('{ArrowDown}{Enter}');
    expect(chips()).toHaveLength(2);
  });

  it('marks selected options with aria-selected', async () => {
    const { user } = renderWithDs(
      <MultiCombobox label="Καλύψεις" items={covers} defaultSelectedKeys={['fire']} />,
    );
    await user.click(input());
    await user.keyboard('{Alt>}{ArrowDown}{/Alt}');
    expect(screen.getByRole('option', { name: 'Πυρκαγιά' })).toHaveAttribute(
      'aria-selected',
      'true',
    );
    expect(screen.getByRole('option', { name: 'Κλοπή' })).toHaveAttribute('aria-selected', 'false');
  });

  it('removes the last chip with Backspace on an empty input', async () => {
    const onSelectionChange = vi.fn();
    const { user } = renderWithDs(
      <MultiCombobox
        label="Καλύψεις"
        items={covers}
        defaultSelectedKeys={['fire', 'theft']}
        onSelectionChange={onSelectionChange}
      />,
    );
    await user.click(input());
    await user.keyboard('{Backspace}');
    expect(onSelectionChange).toHaveBeenLastCalledWith(['fire'], [covers[0]]);
    expect(chips().map((c) => c.textContent)).toEqual(['Πυρκαγιά']);
    await waitFor(() => {
      expect(document.getElementById('ds-live-polite')).toHaveTextContent('Αφαιρέθηκε: Κλοπή');
    });
  });

  it('walks chips with ← from the input start, removes with Delete, returns with →', async () => {
    const { user } = renderWithDs(
      <MultiCombobox
        label="Καλύψεις"
        items={covers}
        defaultSelectedKeys={['fire', 'theft', 'quake']}
      />,
    );
    await user.click(input());
    await user.keyboard('{ArrowLeft}');
    const rows = chips();
    expect(rows[2]).toHaveFocus();
    await user.keyboard('{ArrowLeft}');
    expect(chips()[1]).toHaveFocus();
    await user.keyboard('{Delete}');
    expect(chips().map((c) => c.textContent)).toEqual(['Πυρκαγιά', 'Σεισμός']);
    // Focus stays in the chips; → past the last chip returns to the input.
    const focused = chips().find((c) => c === document.activeElement);
    expect(focused).toBeDefined();
    await user.keyboard('{ArrowRight}{ArrowRight}');
    expect(input()).toHaveFocus();
  });

  it('removes a chip with its × button', async () => {
    const { user } = renderWithDs(
      <MultiCombobox label="Καλύψεις" items={covers} defaultSelectedKeys={['fire', 'theft']} />,
    );
    await user.click(screen.getByRole('button', { name: 'Αφαίρεση Κλοπή' }));
    expect(chips().map((c) => c.textContent)).toEqual(['Πυρκαγιά']);
  });

  it('collapses extra chips into «+N» and expands them', async () => {
    const { user } = renderWithDs(
      <MultiCombobox
        label="Καλύψεις"
        items={covers}
        defaultSelectedKeys={['fire', 'theft', 'quake', 'flood', 'glass']}
        maxVisibleChips={2}
      />,
    );
    expect(chips()).toHaveLength(2);
    const more = screen.getByRole('button', { name: 'Εμφάνιση 3 ακόμη επιλογών' });
    expect(more).toHaveTextContent('+3');
    await user.click(more);
    expect(chips()).toHaveLength(5);
  });

  it('is read-only without remove buttons', () => {
    renderWithDs(
      <MultiCombobox label="Καλύψεις" items={covers} defaultSelectedKeys={['fire']} isReadOnly />,
    );
    expect(screen.queryByRole('button', { name: /Αφαίρεση/ })).not.toBeInTheDocument();
    expect(input()).toHaveAttribute('readonly');
  });

  it('wires required and the error first in aria-describedby', () => {
    renderWithDs(
      <MultiCombobox
        label="Καλύψεις"
        items={covers}
        isRequired
        description="Επιλέξτε μία ή περισσότερες"
        errorMessage="Επιλέξτε τουλάχιστον μία κάλυψη."
      />,
    );
    const box = screen.getByRole('combobox', { name: 'Καλύψεις υποχρεωτικό' });
    expect(box).toHaveAttribute('aria-invalid', 'true');
    expect(box).toHaveAccessibleDescription('Επιλέξτε τουλάχιστον μία κάλυψη.');
  });

  it('keeps a soft-disabled field focusable with its reason', async () => {
    const { user } = renderWithDs(
      <MultiCombobox
        label="Καλύψεις"
        items={covers}
        disabledReason="Το προϊόν δεν επιτρέπει αλλαγές"
      />,
    );
    await user.tab();
    expect(input()).toHaveFocus();
    expect(input()).toHaveAttribute('aria-disabled', 'true');
    expect(input()).toHaveAccessibleDescription('Το προϊόν δεν επιτρέπει αλλαγές');
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<MultiCombobox label="Covers" items={covers} defaultSelectedKeys={['fire']} />);
    expect(screen.getByRole('grid', { name: 'Selected: Covers' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Remove Πυρκαγιά' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container, user } = renderWithDs(
      <MultiCombobox label="Καλύψεις" items={covers} defaultSelectedKeys={['fire', 'theft']} />,
    );
    await expectNoA11yViolations(container);
    await user.type(input(), 'σ');
    await expectNoA11yViolations(document.body);
  });
});
