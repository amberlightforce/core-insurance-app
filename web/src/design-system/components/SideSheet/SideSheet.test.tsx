import { screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import { SideSheet, type SideSheetProps } from './SideSheet';

function Harness(props: Partial<SideSheetProps>) {
  const [open, setOpen] = useState(false);
  return (
    <div style={{ position: 'relative' }}>
      <Button
        onPress={() => {
          setOpen(true);
        }}
      >
        Προσθήκη οχήματος
      </Button>
      <SideSheet
        isOpen={open}
        onClose={() => {
          setOpen(false);
        }}
        title="Νέο όχημα"
        context="ΑΣΦ-2026-004471"
        {...props}
      >
        <label>
          Αριθμός κυκλοφορίας
          <input />
        </label>
      </SideSheet>
    </div>
  );
}

describe('SideSheet', () => {
  it('is a labelled non-modal dialog that focuses the first field and returns focus on Esc', async () => {
    const { user } = renderWithDs(<Harness onSave={vi.fn()} />);
    const trigger = screen.getByRole('button', { name: 'Προσθήκη οχήματος' });
    await user.click(trigger);
    const sheet = screen.getByRole('dialog', { name: 'Νέο όχημα' });
    expect(sheet).toHaveAttribute('aria-modal', 'false');
    expect(sheet).toHaveTextContent('για ΑΣΦ-2026-004471');
    await waitFor(() => {
      expect(within(sheet).getByRole('textbox', { name: 'Αριθμός κυκλοφορίας' })).toHaveFocus();
    });
    // No focus trap: the page stays in the accessibility tree.
    expect(trigger).not.toHaveAttribute('aria-hidden');

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('saves with Ctrl+S and saves and closes with Ctrl+Enter', async () => {
    const onSave = vi.fn(() => Promise.resolve());
    const { user } = renderWithDs(<Harness onSave={onSave} />);
    await user.click(screen.getByRole('button', { name: 'Προσθήκη οχήματος' }));
    await user.keyboard('{Control>}s{/Control}');
    expect(onSave).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(onSave).toHaveBeenCalledTimes(2);
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Νέο όχημα' })).not.toBeInTheDocument();
    });
  });

  it('shows the unsaved dot and asks before closing when dirty', async () => {
    const onDiscard = vi.fn();
    const { user } = renderWithDs(
      <Harness
        isDirty
        changedFields={['Αριθμός κυκλοφορίας']}
        onDiscard={onDiscard}
        onSave={vi.fn()}
      />,
    );
    await user.click(screen.getByRole('button', { name: 'Προσθήκη οχήματος' }));
    const sheet = screen.getByRole('dialog', { name: /Νέο όχημα/ });
    expect(sheet).toHaveTextContent('Μη αποθηκευμένες αλλαγές');

    await user.click(within(sheet).getByRole('button', { name: 'Κλείσιμο' }));
    const confirm = await screen.findByRole('dialog', {
      name: 'Υπάρχουν μη αποθηκευμένες αλλαγές',
    });
    expect(within(confirm).getByRole('button', { name: 'Συνέχεια επεξεργασίας' })).toHaveFocus();
    await user.click(within(confirm).getByRole('button', { name: 'Απόρριψη αλλαγών' }));
    expect(onDiscard).toHaveBeenCalledTimes(1);
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('moves focus to the error banner and marks busy', () => {
    renderWithDs(
      <SideSheet
        isOpen
        onClose={vi.fn()}
        title="Νέο όχημα"
        isBusy
        error="Ο αριθμός κυκλοφορίας υπάρχει ήδη."
        onSave={vi.fn()}
      >
        <input aria-label="Αριθμός" />
      </SideSheet>,
    );
    const sheet = screen.getByRole('dialog');
    expect(sheet).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('alert')).toHaveFocus();
    expect(screen.getByRole('button', { name: 'Άκυρο' })).toHaveAttribute('aria-disabled', 'true');
  });

  it('offers «Επεξεργασία» in read-only mode without a footer', async () => {
    const onEdit = vi.fn();
    const { user } = renderWithDs(
      <SideSheet isOpen onClose={vi.fn()} title="Όχημα ΙΚΑ-1234" isReadOnly onEdit={onEdit}>
        <p>Toyota Yaris · 2019</p>
      </SideSheet>,
    );
    expect(screen.queryByRole('button', { name: 'Άκυρο' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Επεξεργασία' }));
    expect(onEdit).toHaveBeenCalledTimes(1);
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <SideSheet isOpen onClose={vi.fn()} title="New vehicle" context="POL-4471" onSave={vi.fn()}>
        <input aria-label="Plate" />
      </SideSheet>,
    );
    expect(screen.getByRole('dialog')).toHaveTextContent('for POL-4471');
    expect(screen.getByRole('button', { name: /Save/ })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <SideSheet
        isOpen
        onClose={vi.fn()}
        title="Νέο όχημα"
        context="ΑΣΦ-2026-004471"
        isDirty
        onSave={vi.fn()}
      >
        <label>
          Αριθμός κυκλοφορίας
          <input />
        </label>
      </SideSheet>,
    );
    await expectNoA11yViolations(container);
  });
});
