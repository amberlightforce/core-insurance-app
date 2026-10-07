import { act, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { KeyValueList, type KeyValueItem } from './index';

function editable(onCommit: (next: string) => Promise<void>, value = 'Μηνιαία'): KeyValueItem {
  return {
    id: 'pay',
    label: 'Τρόπος πληρωμής',
    value,
    edit: { value, onCommit },
  };
}

describe('KeyValueList', () => {
  it('renders a description list with labels, values, mono ids and empty values', () => {
    const { container } = renderWithDs(
      <KeyValueList
        items={[
          { id: 'p', label: 'Προϊόν', value: 'Αυτοκίνητο ΙΧ' },
          { id: 'r', label: 'Κανόνας', value: 'UW-MOT-014 v7', kind: 'mono' },
          { id: 'e', label: 'Σημειώσεις', value: null },
        ]}
      />,
    );
    const dl = container.querySelector('dl');
    expect(dl).not.toBeNull();
    expect(container.querySelectorAll('dt')).toHaveLength(3);
    expect(screen.getByText('UW-MOT-014 v7').className).toMatch(/mono/);
    expect(container.querySelectorAll('dd')[2]).toHaveTextContent('—κενό');
  });

  it('edits inline: Enter on the pencil opens the editor, Enter commits and shows the receipt', async () => {
    const onCommit = vi.fn(() => Promise.resolve());
    const { user } = renderWithDs(<KeyValueList items={[editable(onCommit)]} />);
    const pencil = screen.getByRole('button', { name: 'Επεξεργασία: Τρόπος πληρωμής' });
    await user.tab();
    expect(pencil).toHaveFocus();
    await user.keyboard('{Enter}');
    const input = screen.getByRole('textbox', { name: 'Τρόπος πληρωμής' });
    expect(input).toHaveFocus();
    expect(input).toHaveValue('Μηνιαία');
    await user.clear(input);
    await user.type(input, 'Ετήσια{Enter}');
    expect(onCommit).toHaveBeenCalledWith('Ετήσια');
    expect(await screen.findByText('Αποθηκεύτηκε')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Επεξεργασία: Τρόπος πληρωμής' })).toHaveFocus();
    });
  });

  it('cancels with Esc and returns focus to the pencil', async () => {
    const onCommit = vi.fn(() => Promise.resolve());
    const { user } = renderWithDs(<KeyValueList items={[editable(onCommit)]} />);
    await user.click(screen.getByText('Μηνιαία'));
    const input = screen.getByRole('textbox', { name: 'Τρόπος πληρωμής' });
    await user.type(input, 'xyz');
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(onCommit).not.toHaveBeenCalled();
    expect(screen.getByText('Μηνιαία')).toBeInTheDocument();
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Επεξεργασία: Τρόπος πληρωμής' })).toHaveFocus();
    });
  });

  it('shows validation errors with aria-invalid and aria-describedby', async () => {
    const { user } = renderWithDs(
      <KeyValueList
        items={[
          {
            id: 'a',
            label: 'Απαλλαγή',
            value: '300',
            edit: {
              value: '300',
              onCommit: () => Promise.resolve(),
              validate: (v) => (/^\d+$/u.test(v) ? null : 'Συμπληρώστε ακέραιο ποσό.'),
            },
          },
        ]}
      />,
    );
    await user.click(screen.getByRole('button', { name: 'Επεξεργασία: Απαλλαγή' }));
    const input = screen.getByRole('textbox', { name: 'Απαλλαγή' });
    await user.type(input, 'α{Enter}');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription('Συμπληρώστε ακέραιο ποσό.');
  });

  it('reverts on failure with a danger message and retries', async () => {
    let attempt = 0;
    const onCommit = vi.fn(() => {
      attempt += 1;
      return attempt === 1 ? Promise.reject(new Error('')) : Promise.resolve();
    });
    const { user, container } = renderWithDs(<KeyValueList items={[editable(onCommit)]} />);
    await user.click(screen.getByRole('button', { name: 'Επεξεργασία: Τρόπος πληρωμής' }));
    const input = screen.getByRole('textbox', { name: 'Τρόπος πληρωμής' });
    await user.clear(input);
    await user.type(input, 'Ετήσια{Enter}');
    const alert = await within(container).findByRole('alert');
    expect(alert).toHaveTextContent('Η αλλαγή δεν αποθηκεύτηκε.');
    expect(screen.getByText('Μηνιαία')).toBeInTheDocument();
    await user.click(within(alert).getByRole('button', { name: 'Επανάληψη' }));
    expect(onCommit).toHaveBeenLastCalledWith('Ετήσια');
    await act(async () => {
      await Promise.resolve();
    });
    expect(within(container).queryByRole('alert')).toBeNull();
  });

  it('never inline-edits fields that need a transaction', async () => {
    const onStart = vi.fn();
    const { user } = renderWithDs(
      <KeyValueList
        items={[
          {
            id: 's',
            label: 'Ασφαλιζόμενο ποσό',
            value: '20.000 €',
            kind: 'money',
            edit: { value: '20000', onCommit: () => Promise.resolve() },
            requiresTransaction: { onStart },
          },
        ]}
      />,
    );
    expect(screen.queryByRole('button', { name: /Επεξεργασία/ })).toBeNull();
    expect(screen.getByText('Απαιτείται πρόσθετη πράξη')).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: 'Έναρξη αλλαγής: Ασφαλιζόμενο ποσό' }));
    expect(onStart).toHaveBeenCalledOnce();
  });

  it('masks permission-limited values and announces them as hidden', () => {
    renderWithDs(
      <KeyValueList items={[{ id: 'i', label: 'IBAN', masked: { display: '•••• 4471' } }]} />,
    );
    expect(screen.getByText('κρυφό')).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<KeyValueList items={[editable(() => Promise.resolve(), 'Monthly')]} />);
    expect(screen.getByRole('button', { name: 'Edit: Τρόπος πληρωμής' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <KeyValueList
        aria-label="Στοιχεία"
        items={[
          editable(() => Promise.resolve()),
          { id: 'x', label: 'Κενό', value: '' },
          {
            id: 't',
            label: 'Κεφάλαιο',
            value: '1.000 €',
            requiresTransaction: { href: '/endorse' },
          },
        ]}
      />,
    );
    await expectNoA11yViolations(container);
  });
});
