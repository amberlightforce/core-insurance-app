import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { parsePercentText, stepValue } from './percent';
import { PercentField } from './PercentField';

const NBSP = '\u00a0';
const NNBSP = '\u202f';
const MINUS = '\u2212';

function field(name: string | RegExp = /Έκπτωση|Συντελεστής|Απόκλιση/) {
  return screen.getByRole('textbox', { name });
}

describe('parsePercentText / stepValue', () => {
  it.each([
    ['15', 'el-GR', 15],
    ['7,5', 'el-GR', 7.5],
    ['-7,5', 'el-GR', -7.5],
    ['\u22127,5', 'el-GR', -7.5],
    ['+2,25', 'el-GR', 2.25],
    ['1.250,5', 'el-GR', 1250.5],
    ['15 %', 'el-GR', 15],
    ['7.5', 'en-GB', 7.5],
    ['1,250.5', 'en-GB', 1250.5],
    ['', 'el-GR', Number.NaN],
    ['abc', 'el-GR', Number.NaN],
  ] as const)('%s (%s) → %s', (text, region, expected) => {
    expect(parsePercentText(text, region)).toBe(expected);
  });

  it('steps without drift and clamps', () => {
    expect(stepValue(0.1, 0.2, 2, 0, 100)).toBe(0.3);
    expect(stepValue(7.53, 0.5, 2, -100, undefined)).toBe(8.03);
    expect(stepValue(99.5, 1, 2, 0, 100)).toBe(100);
    expect(stepValue(-99.5, -5, 2, -100, undefined)).toBe(-100);
  });
});

describe('PercentField', () => {
  it('is a number textbox with the unit described as «τοις εκατό»', () => {
    renderWithDs(<PercentField label="Συντελεστής" defaultValue={15} />);
    const input = field();
    expect(input).toHaveValue('15');
    expect(input).toHaveAccessibleDescription('τοις εκατό');
    expect(screen.getByText('%')).toHaveAttribute('aria-hidden', 'true');
  });

  it('steps by 1 with ↑/↓ and by 10 with Shift, without snapping typed decimals', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <PercentField label="Συντελεστής" defaultValue={15} onChange={onChange} />,
    );
    const input = field();
    await user.click(input);
    await user.keyboard('{ArrowUp}');
    expect(input).toHaveValue('16');
    await user.keyboard('{Shift>}{ArrowUp}{/Shift}');
    expect(input).toHaveValue('26');
    await user.keyboard('{ArrowDown}');
    expect(input).toHaveValue('25');
    await user.clear(input);
    await user.type(input, '7,53');
    await user.keyboard('{ArrowUp}');
    expect(input).toHaveValue('8,53');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith(8.53);
  });

  it('clamps to 0\u2013100 and keeps 2 decimals by default', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<PercentField label="Συντελεστής" onChange={onChange} />);
    await user.type(field(), '150');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith(100);
    await user.clear(field());
    await user.type(field(), '12,3456');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith(12.35);
  });

  it('keeps 4 decimals for rating', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <PercentField label="Συντελεστής" fractionDigits={4} onChange={onChange} />,
    );
    await user.type(field(), '12,3456');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith(12.3456);
    expect(field()).toHaveValue('12,3456');
  });

  it('offers a per-mille variant', () => {
    renderWithDs(<PercentField label="Συντελεστής" perMille defaultValue={2.5} />);
    expect(field()).toHaveValue('2,5');
    expect(field()).toHaveAccessibleDescription('τοις χιλίοις');
    expect(screen.getByText('‰')).toBeInTheDocument();
  });

  it('deviation: signed, 0,5 steps, delta in money and an authority meter', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <PercentField
        label="Απόκλιση"
        variant="deviation"
        defaultValue={-7.5}
        baseAmount="416.00"
        authorityLimit={10}
        onChange={onChange}
      />,
    );
    const input = field();
    expect(input).toHaveValue('-7,5');
    expect(input).toHaveAccessibleDescription(
      `${MINUS}7,50${NNBSP}% = ${MINUS}31,20${NBSP}€ τοις εκατό`,
    );
    const meter = screen.getByRole('meter', { name: `Όριο σας ±10${NNBSP}%` });
    expect(meter).toHaveAttribute('data-status', 'success');

    await user.click(input);
    await user.keyboard('{ArrowDown}');
    expect(input).toHaveValue('-8');
    await user.keyboard('{Shift>}{ArrowDown}{/Shift}');
    expect(input).toHaveValue('-13');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith(-13);
    expect(screen.getByRole('meter')).toHaveAttribute('data-status', 'danger');
    expect(input).toHaveAccessibleDescription(
      expect.stringContaining('Πάνω από το όριο εξουσιοδότησής σας'),
    );
  });

  it('wires required and error first', () => {
    renderWithDs(
      <PercentField
        label="Έκπτωση"
        isRequired
        description="Έως 30 %"
        errorMessage="Η έκπτωση υπερβαίνει το 30 %. Διορθώστε την έκπτωση."
      />,
    );
    const input = screen.getByRole('textbox', { name: 'Έκπτωση υποχρεωτικό' });
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription(
      'Η έκπτωση υπερβαίνει το 30 %. Διορθώστε την έκπτωση. τοις εκατό',
    );
  });

  it('is soft-disabled with a reason and ignores steps', async () => {
    const { user } = renderWithDs(
      <PercentField label="Έκπτωση" defaultValue={5} disabledReason="Κλειδωμένο από την ανάληψη" />,
    );
    await user.tab();
    expect(field()).toHaveFocus();
    expect(field()).toHaveAttribute('aria-disabled', 'true');
    await user.keyboard('{ArrowUp}');
    expect(field()).toHaveValue('5');
  });

  it('follows the en-GB region format', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<PercentField label="Rate" defaultValue={1250.5} maxValue={5000} />, {
      preferences: { regionFormat: 'en-GB' },
    });
    expect(screen.getByRole('textbox', { name: 'Rate' })).toHaveValue('1,250.5');
    expect(screen.getByRole('textbox', { name: 'Rate' })).toHaveAccessibleDescription('percent');
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <PercentField
        label="Απόκλιση"
        variant="deviation"
        defaultValue={-4}
        baseAmount="400"
        authorityLimit={10}
      />,
    );
    await expectNoA11yViolations(container);
  });
});
