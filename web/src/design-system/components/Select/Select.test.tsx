import { act, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Select, type SelectOption } from './Select';

const usage: SelectOption[] = [
  { id: 'private', label: 'Ιδιωτική χρήση', description: 'Μετακινήσεις και αναψυχή' },
  { id: 'professional', label: 'Επαγγελματική χρήση' },
  { id: 'taxi', label: 'Ταξί', isDisabled: true },
];

const assistance: SelectOption[] = [
  { id: 'immediate', label: 'Άμεση' },
  { id: 'basic', label: 'Βασική' },
  { id: 'extended', label: 'Εκτεταμένη' },
];

describe('Select', () => {
  it('is a labelled button that opens a listbox with the placeholder «Επιλέξτε…»', async () => {
    const { user } = renderWithDs(<Select label="Χρήση οχήματος" options={usage} isRequired />);
    const trigger = screen.getByRole('button', { name: /Χρήση οχήματος/ });
    expect(trigger).toHaveTextContent('Επιλέξτε…');
    expect(trigger).toHaveAttribute('aria-haspopup', 'listbox');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    await user.tab();
    await user.keyboard('{ArrowDown}');
    const listbox = await screen.findByRole('listbox');
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    const options = within(listbox).getAllByRole('option');
    expect(options).toHaveLength(3);
    expect(options[2]).toHaveAttribute('aria-disabled', 'true');
    expect(options[0]).toHaveAccessibleDescription('Μετακινήσεις και αναψυχή');
    expect(listbox.closest('[data-material="popover"]')).not.toBeNull();
  });

  it('selects with arrows and Enter, marks the selected option and closes', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <Select label="Χρήση οχήματος" options={usage} onChange={onChange} />,
    );
    await user.tab();
    await user.keyboard('{Enter}');
    await screen.findByRole('listbox');
    await user.keyboard('{ArrowDown}{Enter}');
    expect(onChange).toHaveBeenCalledWith('professional');
    await waitFor(() => {
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });
    const trigger = screen.getByRole('button', { name: /Χρήση οχήματος/ });
    expect(trigger).toHaveTextContent('Επαγγελματική χρήση');
    expect(trigger).toHaveFocus();

    await user.keyboard(' ');
    const selected = await screen.findByRole('option', { name: 'Επαγγελματική χρήση' });
    expect(selected).toHaveAttribute('aria-selected', 'true');
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    });
  });

  it('supports Home and End in the list', async () => {
    const { user } = renderWithDs(<Select label="Οδική βοήθεια" options={assistance} />);
    await user.tab();
    await user.keyboard('{Enter}');
    const listbox = await screen.findByRole('listbox');
    await user.keyboard('{End}');
    expect(within(listbox).getByRole('option', { name: 'Εκτεταμένη' })).toHaveFocus();
    await user.keyboard('{Home}');
    expect(within(listbox).getByRole('option', { name: 'Άμεση' })).toHaveFocus();
  });

  it('matches typeahead without accents or case: «α» finds «Άμεση»', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <Select label="Οδική βοήθεια" options={assistance} onChange={onChange} />,
    );
    await user.tab();
    // Closed trigger: typeahead selects.
    await user.keyboard('ε');
    expect(onChange).toHaveBeenLastCalledWith('extended');
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 1100));
    });
    await user.keyboard('α');
    expect(onChange).toHaveBeenLastCalledWith('immediate');

    // Open list: typeahead moves the highlight.
    await user.keyboard('{Enter}');
    const listbox = await screen.findByRole('listbox');
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 1100));
    });
    await user.keyboard('ΒΑΣ');
    expect(within(listbox).getByRole('option', { name: 'Βασική' })).toHaveFocus();
  });

  it('renders sections with headers', async () => {
    const { user } = renderWithDs(
      <Select
        label="Κάλυψη"
        sections={[
          {
            id: 'basic',
            title: 'Βασικές',
            options: [{ id: 'tpl', label: 'Αστική ευθύνη', code: '01' }],
          },
          {
            id: 'extra',
            title: 'Προαιρετικές',
            options: [{ id: 'ra', label: 'Οδική βοήθεια', code: '03' }],
          },
        ]}
      />,
    );
    await user.click(screen.getByRole('button', { name: /Κάλυψη/ }));
    const groups = await screen.findAllByRole('group');
    expect(groups).toHaveLength(2);
    expect(groups[1]).toHaveAccessibleName('Προαιρετικές');
    expect(screen.getByRole('option', { name: '03 · Οδική βοήθεια' })).toBeInTheDocument();
  });

  it('shows an error with aria-invalid and the error id first', () => {
    renderWithDs(
      <Select
        label="Χρήση οχήματος"
        options={usage}
        helperText="Όπως στην άδεια"
        errorMessage="Επιλέξτε τη χρήση του οχήματος."
      />,
    );
    const trigger = screen.getByRole('button', { name: /Χρήση οχήματος/ });
    expect(trigger).toHaveAttribute('data-invalid', 'true');
    expect(trigger).toHaveAccessibleDescription('Επιλέξτε τη χρήση του οχήματος.');
    const hidden = document.querySelector('select');
    expect(hidden).not.toBeNull();
  });

  it('keeps a disabled-with-reason select focusable and closed', async () => {
    const onChange = vi.fn();
    const reason = 'Η χρήση ορίζεται από το προϊόν';
    const { user } = renderWithDs(
      <Select
        label="Χρήση οχήματος"
        options={usage}
        defaultValue="private"
        disabledReason={reason}
        onChange={onChange}
      />,
    );
    const trigger = screen.getByRole('button', { name: /Χρήση οχήματος/ });
    expect(trigger).toHaveAttribute('aria-disabled', 'true');
    await user.tab();
    expect(trigger).toHaveFocus();
    expect(await screen.findByRole('tooltip')).toHaveTextContent(reason);
    expect(trigger).toHaveAccessibleDescription(new RegExp(reason));
    await user.keyboard('{Enter}');
    await user.keyboard('ε');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
    expect(trigger).toHaveTextContent('Ιδιωτική χρήση');
  });

  it('shows loading as busy with skeleton rows', async () => {
    const { user } = renderWithDs(<Select label="Μοντέλο" options={[]} isLoading />);
    const trigger = screen.getByRole('button', { name: /Μοντέλο/ });
    expect(trigger).toHaveAttribute('aria-busy', 'true');
    await user.click(trigger);
    const listbox = await screen.findByRole('listbox');
    expect(within(listbox).getByText('Φόρτωση επιλογών…').parentElement).toHaveAttribute(
      'aria-busy',
      'true',
    );
  });

  it('renders read-only as plain text', () => {
    renderWithDs(<Select label="Χρήση οχήματος" options={usage} value="private" isReadOnly />);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Χρήση οχήματος' })).toHaveValue('Ιδιωτική χρήση');
  });

  it('uses the English placeholder', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Select label="Vehicle use" options={usage} />);
    expect(screen.getByRole('button', { name: /Vehicle use/ })).toHaveTextContent('Choose…');
  });

  it('has no axe violations', async () => {
    const { container, user } = renderWithDs(
      <div>
        <Select label="Χρήση οχήματος" options={usage} isRequired helperText="Όπως στην άδεια" />
        <Select label="Οδική βοήθεια" options={assistance} errorMessage="Επιλέξτε." />
        <Select label="Κάλυψη" options={assistance} disabledReason="Κλειδωμένο" />
      </div>,
    );
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: /Χρήση οχήματος/ }));
    await screen.findByRole('listbox');
    await expectNoA11yViolations(document.body);
  });
});
