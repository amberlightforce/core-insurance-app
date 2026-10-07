import { screen } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { CurrencyField } from './CurrencyField';

const NBSP = '\u00a0';
const MINUS = '\u2212';

function field(name = /Ποσό/) {
  return screen.getByRole('textbox', { name });
}

describe('CurrencyField', () => {
  it('groups live as you type and keeps the caret at the end', async () => {
    const { user } = renderWithDs(<CurrencyField label="Ποσό" />);
    const input = field();
    await user.type(input, '1234567');
    expect(input).toHaveValue('1.234.567');
    expect((input as HTMLInputElement).selectionStart).toBe(9);
  });

  it('keeps the caret on the same digit when typing in the middle', async () => {
    const { user } = renderWithDs(<CurrencyField label="Ποσό" />);
    const input = field() as HTMLInputElement;
    await user.type(input, '1234');
    expect(input).toHaveValue('1.234');
    // Put the caret after «1.2» and type 9 → «12.934», caret after the 9.
    input.setSelectionRange(3, 3);
    await user.keyboard('9');
    expect(input).toHaveValue('12.934');
    expect(input.selectionStart).toBe(4);
  });

  it('accepts «,» and the numpad «.» as the decimal separator and commits a decimal string', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<CurrencyField label="Ποσό" onChange={onChange} />);
    const input = field();
    await user.type(input, '1234.5');
    expect(input).toHaveValue('1.234,5');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith('1234.50');
    expect(input).toHaveValue('1.234,50');

    await user.clear(input);
    await user.type(input, '99,9');
    await user.keyboard('{Enter}');
    expect(onChange).toHaveBeenLastCalledWith('99.90');
  });

  it('expands shorthand on commit', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<CurrencyField label="Ποσό" onChange={onChange} />);
    await user.type(field(), '12k');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith('12000.00');
    expect(field()).toHaveValue('12.000,00');
    await user.clear(field());
    await user.type(field(), '1,5ε');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith('1500000.00');
  });

  it('rejects extra decimals with the formula message and never rounds', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<CurrencyField label="Ποσό" onChange={onChange} />);
    const input = field();
    await user.type(input, '12,345');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription(
      expect.stringContaining('Το ποσό δέχεται έως 2 δεκαδικά. Διορθώστε το ποσό.'),
    );
    await user.tab();
    expect(onChange).not.toHaveBeenCalled();
    expect(input).toHaveValue('12,345');
    // Fixing it clears the error on change.
    await user.click(input);
    await user.keyboard('{Backspace}');
    expect(input).not.toHaveAttribute('aria-invalid');
  });

  it('puts the error id first in aria-describedby and the unit last', async () => {
    const { user } = renderWithDs(<CurrencyField label="Ποσό" description="Με ΦΠΑ" />);
    const input = field();
    expect(input).toHaveAccessibleDescription('Με ΦΠΑ ευρώ');
    await user.type(input, '1,234');
    const ids = (input.getAttribute('aria-describedby') ?? '').split(' ');
    expect(document.getElementById(ids[0] ?? '')).toHaveTextContent('δεκαδικά');
    expect(document.getElementById(ids[ids.length - 1] ?? '')).toHaveTextContent('ευρώ');
  });

  it('applies the paste rules with an info hint for an interpreted «.»', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<CurrencyField label="Ποσό" onChange={onChange} />);
    const input = field();
    await user.click(input);
    await user.paste('1234.56');
    expect(onChange).toHaveBeenLastCalledWith('1234.56');
    expect(input).toHaveValue('1.234,56');
    expect(input).toHaveAccessibleDescription(expect.stringContaining('Ερμηνεύτηκε ως 1.234,56'));

    await user.clear(input);
    await user.paste('1.234');
    expect(onChange).toHaveBeenLastCalledWith('1234.00');

    await user.clear(input);
    await user.paste('12abc');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription(expect.stringContaining('Μη έγκυρο ποσό'));
  });

  it('does nothing on ↑/↓ (no stepper)', async () => {
    const { user } = renderWithDs(<CurrencyField label="Ποσό" defaultValue="100.00" />);
    await user.click(field());
    await user.keyboard('{ArrowUp}{ArrowDown}{ArrowUp}');
    expect(field()).toHaveValue('100,00');
  });

  it('reverts to the last committed value on Esc', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <CurrencyField label="Ποσό" defaultValue="250.00" onChange={onChange} />,
    );
    const input = field();
    await user.clear(input);
    await user.type(input, '999');
    await user.keyboard('{Escape}');
    expect(input).toHaveValue('250,00');
    await user.tab();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('rejects negatives unless allowed, and shows them with U+2212', async () => {
    const onChange = vi.fn();
    const { user, unmount } = renderWithDs(<CurrencyField label="Ποσό" onChange={onChange} />);
    await user.type(field(), '-50');
    await user.tab();
    expect(onChange).not.toHaveBeenCalled();
    expect(field()).toHaveAccessibleDescription(
      expect.stringContaining('δεν μπορεί να είναι αρνητικό'),
    );
    unmount();

    const { user: user2 } = renderWithDs(
      <CurrencyField label="Ποσό" allowNegative adverse onChange={onChange} />,
    );
    await user2.type(field(), '-50');
    expect(field()).toHaveValue(`${MINUS}50`);
    await user2.tab();
    expect(onChange).toHaveBeenLastCalledWith('-50.00');
    expect(field()).toHaveValue(`${MINUS}50,00`);
    expect(field().closest('[data-adverse]')).not.toBeNull();
  });

  it('warns above the authority limit', async () => {
    const { user } = renderWithDs(<CurrencyField label="Ποσό" authorityLimit="5000" />);
    await user.type(field(), '6000');
    await user.tab();
    expect(field()).toHaveAccessibleDescription(
      expect.stringContaining(
        `Πάνω από το όριο εξουσιοδότησής σας (5.000,00${NBSP}€) · θα σταλεί για έγκριση`,
      ),
    );
    expect(field()).not.toHaveAttribute('aria-invalid');
  });

  it('shows the amount in words, live only when asked', async () => {
    const toWords = vi.fn((value: string) => `λέξεις για ${value}`);
    const { user } = renderWithDs(
      <CurrencyField label="Ποσό" showAmountInWords toWords={toWords} announceWords />,
    );
    await user.type(field(), '12480');
    await user.tab();
    const words = screen.getByText('λέξεις για 12480.00');
    expect(words).toHaveAttribute('aria-live', 'polite');
  });

  it('is controlled with a decimal string', async () => {
    function Controlled() {
      const [value, setValue] = useState<string | null>('10.00');
      return (
        <>
          <CurrencyField label="Ποσό" value={value} onChange={setValue} />
          <output data-testid="value">{String(value)}</output>
        </>
      );
    }
    const { user } = renderWithDs(<Controlled />);
    expect(field()).toHaveValue('10,00');
    await user.clear(field());
    await user.tab();
    expect(screen.getByTestId('value')).toHaveTextContent('null');
  });

  it('formats for the en-GB region with a leading €', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<CurrencyField label="Ποσό" onChange={onChange} />, {
      preferences: { regionFormat: 'en-GB' },
    });
    await user.type(field(), '1234.5');
    expect(field()).toHaveValue('1,234.5');
    await user.tab();
    expect(onChange).toHaveBeenLastCalledWith('1234.50');
    expect(field()).toHaveValue('1,234.50');
  });

  it('is read-only and soft-disabled with a reason', async () => {
    const { user, unmount } = renderWithDs(
      <CurrencyField label="Ποσό" defaultValue="1234.5" isReadOnly />,
    );
    expect(field()).toHaveAttribute('readonly');
    expect(field()).toHaveValue('1.234,50');
    unmount();
    renderWithDs(
      <CurrencyField
        label="Ποσό"
        defaultValue="10"
        disabledReason="Το ποσό ορίζεται από το τιμολόγιο"
      />,
    );
    await user.tab();
    expect(field()).toHaveFocus();
    expect(field()).toHaveAttribute('aria-disabled', 'true');
    expect(field()).toHaveAccessibleDescription('Το ποσό ορίζεται από το τιμολόγιο ευρώ');
  });

  it('renders English messages', async () => {
    await i18n.changeLanguage('en');
    const { user } = renderWithDs(<CurrencyField label="Amount" isRequired />);
    const input = screen.getByRole('textbox', { name: 'Amount required' });
    await user.type(input, '1,234');
    expect(input).toHaveAccessibleDescription(
      'The amount accepts up to 2 decimal places. Correct the amount. euros',
    );
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <CurrencyField label="Ποσό" defaultValue="1234.56" description="Με ΦΠΑ" isRequired />,
    );
    await expectNoA11yViolations(container);
  });
});
