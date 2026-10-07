import { act, screen, waitFor } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { caretIndexFor, formatPhone, parsePhone } from './formatters';
import { SearchField } from './SearchField';
import { TextField } from './TextField';

function describedByIds(el: HTMLElement): string[] {
  return (el.getAttribute('aria-describedby') ?? '').split(' ').filter(Boolean);
}

describe('TextField', () => {
  it('labels the input above it and marks required fields for screen readers', () => {
    renderWithDs(<TextField label="Ονοματεπώνυμο" isRequired />);
    const input = screen.getByRole('textbox', { name: 'Ονοματεπώνυμο υποχρεωτικό' });
    expect(input).toBeRequired();
    expect(screen.getByText('*')).toHaveAttribute('aria-hidden', 'true');
  });

  it('emits the value as the user types (RHF Controller compatible)', async () => {
    const onChange = vi.fn();
    const onBlur = vi.fn();
    const { user } = renderWithDs(
      <TextField label="Email" type="email" name="email" onChange={onChange} onBlur={onBlur} />,
    );
    const input = screen.getByRole('textbox', { name: 'Email' });
    expect(input).toHaveAttribute('type', 'email');
    expect(input).toHaveAttribute('name', 'email');
    await user.type(input, 'a@b.gr');
    expect(onChange).toHaveBeenLastCalledWith('a@b.gr');
    await user.tab();
    expect(onBlur).toHaveBeenCalledTimes(1);
  });

  it('links helper text and shows helper or error, with the error id first', () => {
    const { rerender } = renderWithDs(
      <TextField
        label="Ημερομηνία"
        helperText="ηη/μμ/εεεε"
        warningMessage="Μελλοντική ημερομηνία"
      />,
    );
    let input = screen.getByRole('textbox', { name: 'Ημερομηνία' });
    expect(input).not.toHaveAttribute('aria-invalid');
    expect(input).toHaveAccessibleDescription('Μελλοντική ημερομηνία ηη/μμ/εεεε');

    rerender(
      <TextField
        label="Ημερομηνία"
        helperText="ηη/μμ/εεεε"
        errorMessage="Συμπληρώστε την ημερομηνία ζημίας."
        aria-describedby="extra"
      />,
    );
    input = screen.getByRole('textbox', { name: 'Ημερομηνία' });
    expect(input).toHaveAttribute('aria-invalid', 'true');
    const [first] = describedByIds(input);
    expect(document.getElementById(first ?? '')).toHaveTextContent(
      'Συμπληρώστε την ημερομηνία ζημίας.',
    );
    expect(describedByIds(input)).toContain('extra');
    expect(screen.queryByText('ηη/μμ/εεεε')).not.toBeInTheDocument();
  });

  it('groups a Greek mobile number on blur and emits digits', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<TextField label="Κινητό" type="tel" onChange={onChange} />);
    const input = screen.getByRole('textbox', { name: 'Κινητό' });
    expect(input).toHaveAttribute('type', 'tel');
    expect(input).toHaveAccessibleDescription('Κωδικός χώρας +30');
    await user.type(input, '6941234567');
    expect(onChange).toHaveBeenLastCalledWith('6941234567');
    await user.tab();
    expect(input).toHaveValue('694 123 4567');
  });

  it('keeps a controlled value in sync', () => {
    function Controlled() {
      const [value, setValue] = useState('2101234567');
      return (
        <>
          <TextField label="Τηλέφωνο" type="tel" value={value} onChange={setValue} />
          <button
            type="button"
            onClick={() => {
              setValue('6900000000');
            }}
          >
            reset
          </button>
        </>
      );
    }
    renderWithDs(<Controlled />);
    expect(screen.getByRole('textbox')).toHaveValue('210 123 4567');
    act(() => {
      screen.getByRole('button', { name: 'reset' }).click();
    });
    expect(screen.getByRole('textbox')).toHaveValue('690 000 0000');
  });

  it('submits a textarea with Ctrl+Enter', async () => {
    const onSubmit = vi.fn();
    const { user } = renderWithDs(<TextField label="Σημείωση" multiline onSubmit={onSubmit} />);
    const textarea = screen.getByRole('textbox', { name: 'Σημείωση' });
    expect(textarea.tagName).toBe('TEXTAREA');
    expect(textarea).toHaveAttribute('rows', '3');
    await user.type(textarea, 'Γραμμή{Enter}δεύτερη');
    expect(onSubmit).not.toHaveBeenCalled();
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(textarea).toHaveValue('Γραμμή\nδεύτερη');
  });

  it('announces the character count only when fewer than 20 remain', async () => {
    const { user } = renderWithDs(<TextField label="Τίτλος" maxLength={25} />);
    const input = screen.getByRole('textbox', { name: 'Τίτλος' });
    expect(input).toHaveAttribute('maxlength', '25');
    const live = document.querySelector('[aria-live="polite"]');
    await user.type(input, 'αβγ');
    expect(screen.getByText('3/25')).toBeInTheDocument();
    expect(live).toHaveTextContent('');
    await user.type(input, 'δεζη');
    await waitFor(() => {
      expect(live).toHaveTextContent(/Απομένουν \d+ χαρακτήρες/);
    });
  });

  it('keeps a disabled-with-reason field focusable, describes the reason and blocks edits', async () => {
    const reason = 'Ο ΑΦΜ κλειδώθηκε μετά τον έλεγχο στο gov.gr';
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <TextField
        label="ΑΦΜ"
        defaultValue="090000045"
        disabledReason={reason}
        onChange={onChange}
      />,
    );
    const input = screen.getByRole('textbox', { name: 'ΑΦΜ' });
    expect(input).toHaveAttribute('aria-disabled', 'true');
    expect(input).not.toBeDisabled();
    expect(input).toHaveAccessibleDescription(reason);
    await user.tab();
    expect(input).toHaveFocus();
    expect(await screen.findByRole('tooltip')).toHaveTextContent(reason);
    await user.type(input, '1');
    expect(onChange).not.toHaveBeenCalled();
    expect(input).toHaveValue('090000045');
  });

  it('removes a reasonless disabled field from the tab order', async () => {
    const { user } = renderWithDs(
      <>
        <TextField label="Α" isDisabled />
        <TextField label="Β" />
      </>,
    );
    expect(screen.getByRole('textbox', { name: 'Α' })).toBeDisabled();
    await user.tab();
    expect(screen.getByRole('textbox', { name: 'Β' })).toHaveFocus();
  });

  it('marks loading as busy and shows the spinner after 400 ms', () => {
    vi.useFakeTimers();
    try {
      renderWithDs(<TextField label="ΑΦΜ" isLoading />);
      const input = screen.getByRole('textbox', { name: 'ΑΦΜ' });
      expect(input).toHaveAttribute('aria-busy', 'true');
      const control = input.parentElement;
      expect(control).toHaveAttribute('data-loading', 'true');
      expect(control?.querySelector('svg.ds-spin')).toBeNull();
      act(() => {
        vi.advanceTimersByTime(400);
      });
      expect(control?.querySelector('svg.ds-spin')).not.toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows read-only values with a copy button and MI-10 feedback', async () => {
    const { user } = renderWithDs(
      <TextField label="Αριθμός συμβολαίου" isReadOnly defaultValue="ΑΣΦ-2026-0412" />,
    );
    const input = screen.getByRole('textbox', { name: 'Αριθμός συμβολαίου' });
    expect(input).toHaveAttribute('readonly');
    expect(input.parentElement).toHaveAttribute('data-readonly', 'true');
    const copy = screen.getByRole('button', { name: 'Αντιγραφή: Αριθμός συμβολαίου' });
    await user.click(copy);
    await expect(navigator.clipboard.readText()).resolves.toBe('ΑΣΦ-2026-0412');
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Αντιγράφηκε');
  });

  it('reveals a masked value through an audited callback', async () => {
    const onReveal = vi.fn();
    const { user } = renderWithDs(
      <TextField label="IBAN" isReadOnly isMasked defaultValue="•••• 4471" onReveal={onReveal} />,
    );
    const input = screen.getByRole('textbox', { name: 'IBAN' });
    expect(input).toHaveAccessibleDescription('κρυφό');
    expect(screen.queryByRole('button', { name: /Αντιγραφή/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Εμφάνιση κρυφής τιμής: IBAN' }));
    expect(onReveal).toHaveBeenCalledTimes(1);
  });

  it('accepts an AI suggestion with Enter and rejects it with Delete', async () => {
    const onAccept = vi.fn();
    const onReject = vi.fn();
    const { user } = renderWithDs(
      <TextField
        label="ΔΟΥ"
        defaultValue="Α' Αθηνών"
        isAiSuggested
        aiSource="gov.gr Wallet · 94%"
        onAcceptSuggestion={onAccept}
        onRejectSuggestion={onReject}
      />,
    );
    const input = screen.getByRole('textbox', { name: 'ΔΟΥ' });
    expect(input.parentElement).toHaveAttribute('data-ai', 'true');
    await user.tab();
    await user.tab();
    const badge = screen.getByRole('button', { name: 'Πρόταση ΤΝ: ΔΟΥ' });
    expect(badge).toHaveFocus();
    expect(badge).toHaveAccessibleDescription(/Πατήστε Enter για αποδοχή.*gov\.gr Wallet/);
    await user.keyboard('{Enter}');
    expect(onAccept).toHaveBeenCalledTimes(1);
    await user.keyboard('{Delete}');
    expect(onReject).toHaveBeenCalledTimes(1);
  });

  it('opens the help popover from the label', async () => {
    const { user } = renderWithDs(
      <TextField label="Αριθμός πλαισίου" help="Τον βρίσκετε στην άδεια κυκλοφορίας (πεδίο E)." />,
    );
    await user.click(screen.getByRole('button', { name: 'Βοήθεια: Αριθμός πλαισίου' }));
    expect(await screen.findByRole('dialog')).toHaveTextContent('άδεια κυκλοφορίας');
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('describes a prefix and hides a suffix that is part of the value', () => {
    renderWithDs(
      <>
        <TextField label="Ποσό" prefix="€" />
        <TextField label="Ποσοστό" suffix="%" suffixInValue />
      </>,
    );
    expect(screen.getByRole('textbox', { name: 'Ποσό' })).toHaveAccessibleDescription('€');
    expect(screen.getByText('%')).toHaveAttribute('aria-hidden', 'true');
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<TextField label="Name" isRequired isReadOnly defaultValue="Μ. Παπαδοπούλου" />);
    expect(screen.getByRole('textbox', { name: 'Name required' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Copy: Name' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <TextField label="Ονοματεπώνυμο" isRequired helperText="Όπως στην ταυτότητα" />
        <TextField label="Email" type="email" errorMessage="Ελέγξτε το email." />
        <TextField label="Κινητό" type="tel" warningMessage="Ο αριθμός δεν είναι κινητό" />
        <TextField label="ΑΦΜ" disabledReason="Κλειδωμένο" defaultValue="090000045" />
        <TextField label="IBAN" isReadOnly isMasked defaultValue="•••• 4471" onReveal={vi.fn()} />
        <TextField label="ΔΟΥ" isAiSuggested defaultValue="Α' Αθηνών" />
        <TextField label="Σημείωση" multiline maxLength={200} help="Βοήθεια" />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});

describe('SearchField', () => {
  it('clears on the first Esc and leaves the field on the second', async () => {
    const onClear = vi.fn();
    const { user } = renderWithDs(<SearchField aria-label="Αναζήτηση" onClear={onClear} />);
    const input = screen.getByRole('searchbox', { name: 'Αναζήτηση' });
    await user.click(input);
    await user.keyboard('Παπαδ');
    expect(input).toHaveValue('Παπαδ');
    await user.keyboard('{Escape}');
    expect(input).toHaveValue('');
    expect(onClear).toHaveBeenCalledTimes(1);
    expect(input).toHaveFocus();
    await user.keyboard('{Escape}');
    expect(input).not.toHaveFocus();
  });

  it('submits on Enter and has a clear button outside the tab order', async () => {
    const onSubmit = vi.fn();
    const { user } = renderWithDs(<SearchField label="Αναζήτηση πελάτη" onSubmit={onSubmit} />);
    const input = screen.getByRole('searchbox', { name: 'Αναζήτηση πελάτη' });
    await user.type(input, '4471{Enter}');
    expect(onSubmit).toHaveBeenCalledWith('4471');
    expect(screen.getByRole('button', { name: 'Καθαρισμός αναζήτησης' })).toHaveAttribute(
      'tabindex',
      '-1',
    );
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(<SearchField label="Αναζήτηση" defaultValue="4471" />);
    await expectNoA11yViolations(container);
  });
});

describe('formatters', () => {
  it('parses and groups Greek phone numbers', () => {
    expect(parsePhone('+30 694 123 4567')).toBe('6941234567');
    expect(parsePhone('0030 210 1234567')).toBe('2101234567');
    expect(formatPhone('6941234567')).toBe('694 123 4567');
    expect(formatPhone('69412')).toBe('69412');
  });

  it('places the caret after the n-th significant character', () => {
    const parse = (text: string) => text.replace(/\s/g, '');
    expect(caretIndexFor('090 000 045', 3, parse)).toBe(3);
    expect(caretIndexFor('090 000 045', 4, parse)).toBe(5);
    expect(caretIndexFor('090 000 045', 0, parse)).toBe(0);
  });
});
