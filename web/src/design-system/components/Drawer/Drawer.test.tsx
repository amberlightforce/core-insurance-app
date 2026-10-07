import { screen, waitFor, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import { Drawer, type DrawerProps } from './Drawer';

function Harness(props: Partial<DrawerProps>) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <Button
        onPress={() => {
          setOpen(true);
        }}
      >
        Φίλτρα
      </Button>
      <Button>Άλλο</Button>
      <Drawer
        title="Φίλτρα αναζήτησης"
        isOpen={open}
        onOpenChange={setOpen}
        footer={
          <>
            <Button variant="ghost">Εκκαθάριση</Button>
            <Button variant="primary">Εφαρμογή (124)</Button>
          </>
        }
        {...props}
      >
        <label>
          Κατάσταση
          <input />
        </label>
      </Drawer>
    </>
  );
}

describe('Drawer (modal)', () => {
  it('is a modal dialog that traps focus and closes with Esc', async () => {
    const { user } = renderWithDs(<Harness />);
    const trigger = screen.getByRole('button', { name: 'Φίλτρα' });
    await user.click(trigger);
    const dialog = await screen.findByRole('dialog', { name: 'Φίλτρα αναζήτησης' });
    expect(dialog.closest('[data-modal]')).toHaveAttribute('data-modal', 'true');
    for (let i = 0; i < 6; i++) {
      await user.tab();
      expect(dialog.contains(document.activeElement)).toBe(true);
    }
    expect(within(dialog).getByRole('button', { name: 'Εφαρμογή (124)' })).toBeInTheDocument();
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('shows a busy skeleton while loading and a retry banner on error', async () => {
    const onRetry = vi.fn();
    const { user, rerender } = renderWithDs(
      <Drawer title="Φίλτρα" isOpen onOpenChange={vi.fn()} status="loading">
        <p>Περιεχόμενο</p>
      </Drawer>,
    );
    const dialog = await screen.findByRole('dialog');
    expect(dialog.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(within(dialog).queryByText('Περιεχόμενο')).not.toBeInTheDocument();
    rerender(
      <Drawer title="Φίλτρα" isOpen onOpenChange={vi.fn()} status="error" onRetry={onRetry}>
        <p>Περιεχόμενο</p>
      </Drawer>,
    );
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Δεν ήταν δυνατή η φόρτωση');
    await user.click(within(dialog).getByRole('button', { name: 'Επανάληψη' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('has no axe violations', async () => {
    renderWithDs(
      <Drawer title="Φίλτρα" isOpen onOpenChange={vi.fn()}>
        <p>Περιεχόμενο</p>
      </Drawer>,
    );
    const dialog = await screen.findByRole('dialog');
    await expectNoA11yViolations(dialog.closest('[data-modal]') ?? dialog);
  });
});

describe('Drawer (non-modal)', () => {
  it('is a labelled complementary landmark without a focus trap; Esc closes and restores focus', async () => {
    const { user } = renderWithDs(<Harness isModal={false} width={400} />);
    const trigger = screen.getByRole('button', { name: 'Φίλτρα' });
    await user.click(trigger);
    const panel = await screen.findByRole('complementary', { name: 'Φίλτρα αναζήτησης' });
    expect(panel).toHaveAttribute('data-width', '400');
    expect(panel).not.toHaveAttribute('aria-modal');
    await waitFor(() => {
      expect(panel).toHaveFocus();
    });
    // No trap: the other page button stays reachable.
    expect(screen.getByRole('button', { name: 'Άλλο' })).not.toHaveAttribute('aria-hidden');

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('is reachable with F6', async () => {
    const { user } = renderWithDs(
      <>
        <Button>Εκτός</Button>
        <Drawer title="Ειδοποιήσεις" isOpen isModal={false} onOpenChange={vi.fn()}>
          <p>Καμία</p>
        </Drawer>
      </>,
    );
    const panel = await screen.findByRole('complementary', { name: 'Ειδοποιήσεις' });
    screen.getByRole('button', { name: 'Εκτός' }).focus();
    await user.keyboard('{F6}');
    await waitFor(() => {
      expect(panel.contains(document.activeElement)).toBe(true);
    });
  });

  it('translates the close button', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <Drawer title="Notifications" isOpen isModal={false} onOpenChange={vi.fn()}>
        <p>None</p>
      </Drawer>,
    );
    expect(await screen.findByRole('button', { name: 'Close' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderWithDs(
      <Drawer title="Ειδοποιήσεις" isOpen isModal={false} onOpenChange={vi.fn()}>
        <p>Καμία</p>
      </Drawer>,
    );
    const panel = await screen.findByRole('complementary');
    await expectNoA11yViolations(panel);
  });
});
