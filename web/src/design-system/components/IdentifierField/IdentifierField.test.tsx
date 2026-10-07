import { act, createEvent, fireEvent, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { IdentifierField } from './IdentifierField';

describe('IdentifierField', () => {
  it('groups an ΑΦΜ as «000 000 000» while typing and emits the ungrouped value', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<IdentifierField kind="afm" label="ΑΦΜ" onChange={onChange} />);
    const input = screen.getByRole('textbox', { name: 'ΑΦΜ' });
    expect(input).toHaveAttribute('autocomplete', 'off');
    expect(input).toHaveAttribute('spellcheck', 'false');
    expect(input).toHaveAttribute('inputmode', 'numeric');
    expect(input.parentElement).toHaveAttribute('data-mono', 'true');
    await user.type(input, '090000045');
    expect(input).toHaveValue('090 000 045');
    expect(onChange).toHaveBeenLastCalledWith('090000045');
  });

  it('ignores letters in an ΑΦΜ and keeps the caret after the typed digit', async () => {
    const { user } = renderWithDs(
      <IdentifierField kind="afm" label="ΑΦΜ" defaultValue="0900045" />,
    );
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'ΑΦΜ' });
    expect(input).toHaveValue('090 004 5');
    // Caret after «090 0», type «0» then a letter → «090 000 45», caret stays after the new digit.
    await user.click(input);
    input.setSelectionRange(5, 5);
    await user.keyboard('0x');
    expect(input).toHaveValue('090 000 45');
    expect(input.selectionStart).toBe(6);
  });

  it('validates on blur with a «what + how to fix» message, then live', async () => {
    const onValidityChange = vi.fn();
    const { user } = renderWithDs(
      <IdentifierField kind="afm" label="ΑΦΜ" onValidityChange={onValidityChange} />,
    );
    const input = screen.getByRole('textbox', { name: 'ΑΦΜ' });
    await user.type(input, '09000004');
    expect(input).not.toHaveAttribute('aria-invalid');
    await user.tab();
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription('Ο ΑΦΜ πρέπει να έχει 9 ψηφία. Ελέγξτε τον αριθμό.');
    await user.type(input, '6');
    expect(input).toHaveAccessibleDescription(/ψηφίο ελέγχου/);
    await user.type(input, '{Backspace}5');
    expect(input).not.toHaveAttribute('aria-invalid');
    expect(onValidityChange).toHaveBeenLastCalledWith(null);
  });

  it('lets an external error message win', () => {
    renderWithDs(
      <IdentifierField
        kind="afm"
        label="ΑΦΜ"
        defaultValue="090000045"
        errorMessage="Δεν βρέθηκε"
      />,
    );
    expect(screen.getByRole('textbox')).toHaveAccessibleDescription('Δεν βρέθηκε');
  });

  it('formats an IBAN in groups of 4 and validates mod 97', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<IdentifierField kind="iban" label="IBAN" onChange={onChange} />);
    const input = screen.getByRole('textbox', { name: 'IBAN' });
    await user.type(input, 'gr1601101250000000012300695');
    expect(input).toHaveValue('GR16 0110 1250 0000 0001 2300 695');
    expect(onChange).toHaveBeenLastCalledWith('GR1601101250000000012300695');
    await user.tab();
    expect(input).not.toHaveAttribute('aria-invalid');

    await user.clear(input);
    await user.type(input, 'GR1701101250000000012300695');
    await user.tab();
    expect(input).toHaveAccessibleDescription(/ψηφία ελέγχου δεν ταιριάζουν/);
  });

  it('copies the ungrouped value', () => {
    renderWithDs(
      <IdentifierField kind="iban" label="IBAN" defaultValue="GR1601101250000000012300695" />,
    );
    const input = screen.getByRole<HTMLInputElement>('textbox', { name: 'IBAN' });
    input.setSelectionRange(0, input.value.length);
    const setData = vi.fn();
    const event = createEvent.copy(input, { clipboardData: { setData } });
    fireEvent(input, event);
    expect(setData).toHaveBeenCalledWith('text/plain', 'GR1601101250000000012300695');
  });

  it('converts Latin lookalikes on a plate to Greek capitals with a 1 s hint', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <IdentifierField kind="plate" label="Αριθμός κυκλοφορίας" onChange={onChange} />,
    );
    const input = screen.getByRole('textbox', { name: 'Αριθμός κυκλοφορίας' });
    await user.type(input, 'ikx1234');
    expect(input).toHaveValue('ΙΚΧ-1234');
    expect(onChange).toHaveBeenLastCalledWith('ΙΚΧ1234');
    expect(screen.getByRole('status')).toHaveTextContent('Μετατράπηκε σε ελληνικούς χαρακτήρες');
    await waitFor(
      () => {
        expect(screen.queryByText('Μετατράπηκε σε ελληνικούς χαρακτήρες')).not.toBeInTheDocument();
      },
      { timeout: 2000 },
    );
  });

  it('rejects plate letters outside the 14 shared letters', async () => {
    const { user } = renderWithDs(<IdentifierField kind="plate" label="Πινακίδα" />);
    const input = screen.getByRole('textbox', { name: 'Πινακίδα' });
    await user.type(input, 'ΓΚΧ1234');
    await user.tab();
    expect(input).toHaveAccessibleDescription(/Α Β Ε Ζ Η Ι Κ Μ Ν Ο Ρ Τ Υ Χ/);
  });

  it('treats a VIN check digit mismatch as a warning only', async () => {
    const { user } = renderWithDs(<IdentifierField kind="vin" label="Αριθμός πλαισίου" />);
    const input = screen.getByRole('textbox', { name: 'Αριθμός πλαισίου' });
    await user.type(input, 'wvwzzz1jzxw000001');
    expect(input).toHaveValue('WVWZZZ1JZXW000001');
    await user.tab();
    expect(input).not.toHaveAttribute('aria-invalid');
    expect(input.parentElement).toHaveAttribute('data-warning', 'true');
    expect(input).toHaveAccessibleDescription(/ψηφίο ελέγχου/);
  });

  it('rejects I, O and Q in a VIN', async () => {
    const { user } = renderWithDs(<IdentifierField kind="vin" label="VIN" />);
    const input = screen.getByRole('textbox', { name: 'VIN' });
    await user.type(input, '1M8GDM9AXKP04278O');
    await user.tab();
    expect(input).toHaveAttribute('aria-invalid', 'true');
  });

  it('validates a reference against the scheme pattern', async () => {
    const { user } = renderWithDs(
      <IdentifierField kind="reference" label="Αριθμός ζημίας" referencePattern={/^ΖΗΜ-\d{6}$/u} />,
    );
    const input = screen.getByRole('textbox', { name: 'Αριθμός ζημίας' });
    await user.type(input, 'ζημ-12');
    expect(input).toHaveValue('ΖΗΜ-12');
    await user.tab();
    expect(input).toHaveAttribute('aria-invalid', 'true');
  });

  it('shows messages in English', async () => {
    await i18n.changeLanguage('en');
    const { user } = renderWithDs(<IdentifierField kind="afm" label="Tax number" />);
    const input = screen.getByRole('textbox', { name: 'Tax number' });
    await user.type(input, '123');
    act(() => {
      input.blur();
    });
    expect(input).toHaveAccessibleDescription(
      'The tax number (ΑΦΜ) must have 9 digits. Check the number.',
    );
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <IdentifierField kind="afm" label="ΑΦΜ" defaultValue="090000045" isRequired />
        <IdentifierField
          kind="iban"
          label="IBAN"
          defaultValue="GR1601101250000000012300695"
          isReadOnly
        />
        <IdentifierField kind="plate" label="Πινακίδα" errorMessage="Η πινακίδα δεν βρέθηκε." />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
