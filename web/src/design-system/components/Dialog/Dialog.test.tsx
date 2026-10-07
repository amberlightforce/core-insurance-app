import { screen, waitFor, within } from '@testing-library/react';
import { FilePen } from 'lucide-react';
import { useState } from 'react';
import { DialogTrigger } from 'react-aria-components';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import { ConfirmDialog } from './ConfirmDialog';
import { Dialog, type DialogAction } from './Dialog';
import { UnsavedChangesDialog } from './UnsavedChangesDialog';

function NoteDialog({ onSave }: { onSave: DialogAction['onAction'] }) {
  return (
    <DialogTrigger>
      <Button>Νέα σημείωση</Button>
      <Dialog
        title="Νέα σημείωση"
        icon={FilePen}
        size="sm"
        primaryAction={{ label: 'Αποθήκευση', onAction: onSave }}
      >
        <label>
          Κείμενο
          <input />
        </label>
      </Dialog>
    </DialogTrigger>
  );
}

describe('Dialog', () => {
  it('opens a labelled modal, focuses the first field and returns focus on Esc', async () => {
    const { user } = renderWithDs(<NoteDialog onSave={() => undefined} />);
    const trigger = screen.getByRole('button', { name: 'Νέα σημείωση' });
    await user.click(trigger);
    const dialog = await screen.findByRole('dialog', { name: 'Νέα σημείωση' });
    expect(dialog.closest('[data-material]')).toHaveAttribute('data-material', 'overlay');
    expect(dialog.closest('[data-size]')).toHaveAttribute('data-size', 'sm');
    expect(within(dialog).getByRole('textbox', { name: 'Κείμενο' })).toHaveFocus();

    // Footer order: «Άκυρο» first, primary last.
    const buttons = within(dialog).getAllByRole('button');
    expect(buttons.map((b) => b.textContent)).toEqual(['', 'Άκυρο', 'Αποθήκευση']);

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('traps focus inside the dialog', async () => {
    const { user } = renderWithDs(<NoteDialog onSave={() => undefined} />);
    await user.click(screen.getByRole('button', { name: 'Νέα σημείωση' }));
    const dialog = await screen.findByRole('dialog');
    for (let i = 0; i < 5; i++) {
      await user.tab();
      expect(dialog.contains(document.activeElement)).toBe(true);
    }
  });

  it('runs the primary with Ctrl+Enter and closes', async () => {
    const onSave = vi.fn();
    const { user } = renderWithDs(<NoteDialog onSave={onSave} />);
    await user.click(screen.getByRole('button', { name: 'Νέα σημείωση' }));
    await screen.findByRole('dialog');
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(onSave).toHaveBeenCalledTimes(1);
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('is busy while the action runs: caption, Esc blocked, other buttons disabled', async () => {
    let resolve: () => void = () => undefined;
    const onSave = vi.fn(
      () =>
        new Promise<void>((r) => {
          resolve = r;
        }),
    );
    const { user } = renderWithDs(<NoteDialog onSave={onSave} />);
    await user.click(screen.getByRole('button', { name: 'Νέα σημείωση' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Αποθήκευση' }));

    expect(within(dialog).getByRole('status')).toHaveTextContent('Η ενέργεια εκτελείται…');
    expect(within(dialog).getByRole('button', { name: 'Άκυρο' })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await user.keyboard('{Escape}');
    expect(screen.getByRole('dialog')).toBeInTheDocument();

    resolve();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('keeps the input and shows a danger banner when the action fails', async () => {
    const { user } = renderWithDs(<NoteDialog onSave={() => Promise.reject(new Error('x'))} />);
    await user.click(screen.getByRole('button', { name: 'Νέα σημείωση' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByRole('textbox'), 'Επικοινωνία με πελάτη');
    await user.click(within(dialog).getByRole('button', { name: 'Αποθήκευση' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(
      'Η ενέργεια δεν ολοκληρώθηκε',
    );
    expect(within(dialog).getByRole('textbox')).toHaveValue('Επικοινωνία με πελάτη');
  });

  it('marks a nested modal as the second layer', async () => {
    function Nested() {
      const [inner, setInner] = useState(false);
      return (
        <Dialog title="Εξωτερικό" isOpen hideCancel>
          <Button
            onPress={() => {
              setInner(true);
            }}
          >
            Άνοιγμα
          </Button>
          <Dialog title="Εσωτερικό" isOpen={inner} onOpenChange={setInner} hideCancel>
            <p>Λεπτομέρειες</p>
          </Dialog>
        </Dialog>
      );
    }
    const { user } = renderWithDs(<Nested />);
    await user.click(await screen.findByRole('button', { name: 'Άνοιγμα' }));
    const inner = await screen.findByRole('dialog', { name: 'Εσωτερικό' });
    expect(inner.closest('[data-depth]')).toHaveAttribute('data-depth', '2');
  });

  it('translates the chrome', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Dialog title="New note" isOpen />);
    const dialog = await screen.findByRole('dialog', { name: 'New note' });
    expect(within(dialog).getByRole('button', { name: 'Close' })).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
  });

  it('has no axe violations when open', async () => {
    renderWithDs(
      <Dialog
        title="Νέα σημείωση"
        isOpen
        primaryAction={{ label: 'Αποθήκευση', onAction: vi.fn() }}
      >
        <p>Σώμα</p>
      </Dialog>,
    );
    const dialog = await screen.findByRole('dialog');
    await expectNoA11yViolations(dialog.closest('[data-depth]') ?? dialog);
  });
});

describe('ConfirmDialog', () => {
  it('level 1: alertdialog with «Άκυρο» focused and no Ctrl+Enter', async () => {
    const onConfirm = vi.fn();
    const { user } = renderWithDs(
      <ConfirmDialog
        level={1}
        isOpen
        title="Απόσυρση προσφοράς ΠΡΦ-2026-0412;"
        consequence="Η προσφορά δεν θα μπορεί να δεσμευτεί."
        confirmLabel="Απόσυρση προσφοράς"
        onConfirm={onConfirm}
      />,
    );
    const dialog = await screen.findByRole('alertdialog', {
      name: 'Απόσυρση προσφοράς ΠΡΦ-2026-0412;',
    });
    expect(dialog.closest('[data-size]')).toHaveAttribute('data-size', 'sm');
    expect(within(dialog).getByRole('button', { name: 'Άκυρο' })).toHaveFocus();
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(onConfirm).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole('button', { name: 'Απόσυρση προσφοράς' }));
    expect(onConfirm).toHaveBeenCalledWith({ reason: null });
  });

  it('level 2: enables the danger button only with a reason and the typed token', async () => {
    const onConfirm = vi.fn();
    const { user } = renderWithDs(
      <ConfirmDialog
        level={2}
        isOpen
        title="Ακύρωση ασφαλιστηρίου ΑΣΦ-2026-004471;"
        consequences={[
          { label: 'Ημερομηνία ισχύος', value: '15/10/2026' },
          { label: 'Επιστροφή', value: '128,40 €' },
        ]}
        reasons={[
          { id: 'customer', label: 'Αίτημα πελάτη' },
          { id: 'non-payment', label: 'Μη πληρωμή' },
        ]}
        confirmToken="4471"
        confirmLabel="Ακύρωση ασφαλιστηρίου"
        onConfirm={onConfirm}
      />,
    );
    const dialog = await screen.findByRole('alertdialog');
    expect(dialog.closest('[data-size]')).toHaveAttribute('data-size', 'md');
    // D-FE-17: a dialog with fields focuses the first field (the reason select).
    expect(within(dialog).getByRole('button', { name: /Αιτία/ })).toHaveFocus();
    expect(within(dialog).getByText('128,40 €')).toBeInTheDocument();
    const danger = within(dialog).getByRole('button', { name: 'Ακύρωση ασφαλιστηρίου' });
    expect(danger).toHaveAttribute('aria-disabled', 'true');
    expect(danger).toHaveAccessibleDescription('Επιλέξτε αιτία για να συνεχίσετε');

    await user.click(within(dialog).getByRole('button', { name: /Αιτία/ }));
    await user.click(await screen.findByRole('option', { name: 'Μη πληρωμή' }));
    expect(danger).toHaveAccessibleDescription('Πληκτρολογήστε 4471 για επιβεβαίωση');

    await user.type(
      within(dialog).getByRole('textbox', { name: 'Πληκτρολογήστε 4471 για επιβεβαίωση' }),
      '4471',
    );
    // The enabled button drops its reason tooltip wrapper, so query it again.
    const enabled = within(dialog).getByRole('button', { name: 'Ακύρωση ασφαλιστηρίου' });
    expect(enabled).not.toHaveAttribute('aria-disabled');
    await user.click(enabled);
    expect(onConfirm).toHaveBeenCalledWith({ reason: 'non-payment' });
  });

  it('level 3: sends for approval with the user-check wording', async () => {
    renderWithDs(
      <ConfirmDialog
        level={3}
        isOpen
        title="Συγχώνευση προσώπων ΠΡΣ-1182 και ΠΡΣ-2040;"
        confirmToken="2040"
        approverGroup="Διαχείριση δεδομένων"
        confirmLabel="Συγχώνευση προσώπων"
        onConfirm={vi.fn()}
      />,
    );
    const dialog = await screen.findByRole('alertdialog');
    expect(
      within(dialog).getByRole('button', { name: 'Αποστολή για έγκριση' }),
    ).toBeInTheDocument();
    expect(dialog).toHaveTextContent('Θα σταλεί για έγκριση στην ομάδα «Διαχείριση δεδομένων»');
  });

  it('has no axe violations (level 2)', async () => {
    renderWithDs(
      <ConfirmDialog
        level={2}
        isOpen
        title="Ακύρωση πληρωμής ΠΛΗ-2026-0091;"
        consequences={[{ label: 'Ποσό', value: '500,00 €' }]}
        reasons={[{ id: 'dup', label: 'Διπλή πληρωμή' }]}
        confirmToken="0091"
        confirmLabel="Ακύρωση πληρωμής"
        onConfirm={vi.fn()}
      />,
    );
    const dialog = await screen.findByRole('alertdialog');
    await expectNoA11yViolations(dialog.closest('[data-depth]') ?? dialog);
  });
});

describe('UnsavedChangesDialog', () => {
  const fields = [
    'Οδηγός',
    'Ημερομηνία',
    'Ποσό',
    'Συνεργείο',
    'Σημείωση',
    'Πραγματογνώμονας',
    'Τόπος',
  ];

  it('lists five fields then «και 2 ακόμη», focuses «Συνέχεια επεξεργασίας»', async () => {
    const onDiscard = vi.fn();
    const { user } = renderWithDs(
      <UnsavedChangesDialog isOpen changedFields={fields} onDiscard={onDiscard} onSave={vi.fn()} />,
    );
    const dialog = await screen.findByRole('dialog', { name: 'Υπάρχουν μη αποθηκευμένες αλλαγές' });
    const items = within(dialog).getAllByRole('listitem');
    expect(items).toHaveLength(6);
    expect(items[5]).toHaveTextContent('και 2 ακόμη');
    expect(within(dialog).getByRole('button', { name: 'Συνέχεια επεξεργασίας' })).toHaveFocus();
    const order = within(dialog)
      .getAllByRole('button')
      .map((b) => b.textContent);
    expect(order.slice(1)).toEqual([
      'Συνέχεια επεξεργασίας',
      'Απόρριψη αλλαγών',
      'Αποθήκευση και έξοδος',
    ]);
    await user.click(within(dialog).getByRole('button', { name: 'Απόρριψη αλλαγών' }));
    expect(onDiscard).toHaveBeenCalledTimes(1);
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <UnsavedChangesDialog
        isOpen
        changedFields={['Driver']}
        onDiscard={vi.fn()}
        onSave={vi.fn()}
      />,
    );
    expect(
      await screen.findByRole('dialog', { name: 'You have unsaved changes' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Keep editing' })).toBeInTheDocument();
  });
});
