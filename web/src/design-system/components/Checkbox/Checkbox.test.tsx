import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Checkbox, CheckboxGroup } from './Checkbox';

describe('Checkbox', () => {
  it('is a native checkbox toggled with Space', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<Checkbox onChange={onChange}>Οδική βοήθεια</Checkbox>);
    const box = screen.getByRole('checkbox', { name: 'Οδική βοήθεια' });
    expect(box.tagName).toBe('INPUT');
    await user.tab();
    expect(box).toHaveFocus();
    await user.keyboard(' ');
    expect(box).toBeChecked();
    expect(onChange).toHaveBeenLastCalledWith(true);
    expect(box.closest('label')).toHaveAttribute('data-selected', 'true');
  });

  it('exposes the mixed state', () => {
    renderWithDs(<Checkbox isIndeterminate>Επιλογή όλων</Checkbox>);
    const box = screen.getByRole<HTMLInputElement>('checkbox', { name: 'Επιλογή όλων' });
    expect(box.indeterminate).toBe(true);
    expect(box).toBePartiallyChecked();
  });

  it('links its description', () => {
    renderWithDs(<Checkbox description="Έως 3 συμβάντα ανά έτος">Οδική βοήθεια</Checkbox>);
    expect(screen.getByRole('checkbox')).toHaveAccessibleDescription('Έως 3 συμβάντα ανά έτος');
  });

  it('disables without leaving a checked look ambiguous', () => {
    renderWithDs(
      <Checkbox isDisabled defaultSelected>
        Αστική ευθύνη
      </Checkbox>,
    );
    const box = screen.getByRole('checkbox', { name: 'Αστική ευθύνη' });
    expect(box).toBeDisabled();
    expect(box).toBeChecked();
    expect(box.closest('label')).toHaveAttribute('data-disabled', 'true');
  });

  it('shows read-only values as a check icon or «—» and ignores Space', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <>
        <Checkbox isReadOnly defaultSelected onChange={onChange}>
          Θραύση κρυστάλλων
        </Checkbox>
        <Checkbox isReadOnly>Πυρκαγιά</Checkbox>
      </>,
    );
    const checked = screen.getByRole('checkbox', { name: 'Θραύση κρυστάλλων' });
    expect(checked).toHaveAttribute('aria-readonly', 'true');
    expect(screen.getByText('—')).toHaveAttribute('aria-hidden', 'true');
    await user.tab();
    await user.keyboard(' ');
    expect(checked).toBeChecked();
    expect(onChange).not.toHaveBeenCalled();
  });
});

describe('CheckboxGroup', () => {
  it('is a labelled group; Tab moves between boxes and values are collected', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <CheckboxGroup label="Πρόσθετες καλύψεις" onChange={onChange} isRequired>
        <Checkbox value="glass">Θραύση κρυστάλλων</Checkbox>
        <Checkbox value="fire">Πυρκαγιά</Checkbox>
      </CheckboxGroup>,
    );
    const group = screen.getByRole('group', { name: 'Πρόσθετες καλύψεις υποχρεωτικό' });
    expect(group).toBeInTheDocument();
    await user.tab();
    expect(screen.getByRole('checkbox', { name: 'Θραύση κρυστάλλων' })).toHaveFocus();
    await user.keyboard(' ');
    await user.tab();
    expect(screen.getByRole('checkbox', { name: 'Πυρκαγιά' })).toHaveFocus();
    await user.keyboard(' ');
    expect(onChange).toHaveBeenLastCalledWith(['glass', 'fire']);
  });

  it('shows a group error and marks every box invalid', () => {
    renderWithDs(
      <CheckboxGroup label="Συναινέσεις" errorMessage="Επιλέξτε τουλάχιστον μία συναίνεση.">
        <Checkbox value="a">Επικοινωνία με email</Checkbox>
        <Checkbox value="b">Επικοινωνία με SMS</Checkbox>
      </CheckboxGroup>,
    );
    const group = screen.getByRole('group', { name: 'Συναινέσεις' });
    expect(group).toHaveAccessibleDescription('Επιλέξτε τουλάχιστον μία συναίνεση.');
    for (const box of screen.getAllByRole('checkbox')) {
      expect(box).toHaveAttribute('aria-invalid', 'true');
    }
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <CheckboxGroup label="Extras" isRequired>
        <Checkbox value="a">Glass</Checkbox>
      </CheckboxGroup>,
    );
    expect(screen.getByRole('group', { name: 'Extras required' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <CheckboxGroup label="Καλύψεις" helperText="Επιλέξτε όσες χρειάζεστε">
          <Checkbox value="a" description="Έως 3 συμβάντα">
            Οδική βοήθεια
          </Checkbox>
          <Checkbox value="b" isDisabled defaultSelected>
            Αστική ευθύνη
          </Checkbox>
        </CheckboxGroup>
        <Checkbox isIndeterminate>Επιλογή όλων</Checkbox>
        <Checkbox isReadOnly defaultSelected>
          Πυρκαγιά
        </Checkbox>
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
