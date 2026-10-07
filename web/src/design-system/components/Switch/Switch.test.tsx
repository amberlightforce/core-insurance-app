import { act, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Switch } from './Switch';

describe('Switch', () => {
  it('is a switch toggled with Space and Enter', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<Switch onChange={onChange}>Εορτασμοί</Switch>);
    const toggle = screen.getByRole('switch', { name: 'Εορτασμοί' });
    expect(toggle).not.toBeChecked();
    await user.tab();
    expect(toggle).toHaveFocus();
    await user.keyboard(' ');
    expect(toggle).toBeChecked();
    expect(onChange).toHaveBeenLastCalledWith(true);
    await user.keyboard('{Enter}');
    expect(toggle).not.toBeChecked();
  });

  it('shows the optional state text and describes the setting', async () => {
    const { user } = renderWithDs(
      <Switch showStateText description="Κινούμενα εφέ σε ορόσημα">
        Εορτασμοί
      </Switch>,
    );
    expect(screen.getByText('Ανενεργό')).toBeInTheDocument();
    const toggle = screen.getByRole('switch', { name: 'Εορτασμοί' });
    expect(toggle).toHaveAccessibleDescription('Κινούμενα εφέ σε ορόσημα');
    await user.click(toggle);
    expect(screen.getByText('Ενεργό')).toBeInTheDocument();
  });

  it('persists through onChangeAsync with a spinner, then commits', async () => {
    let resolve: () => void = () => undefined;
    const onChange = vi.fn();
    const onChangeAsync = vi.fn(
      () =>
        new Promise<void>((r) => {
          resolve = r;
        }),
    );
    const { user } = renderWithDs(
      <Switch onChangeAsync={onChangeAsync} onChange={onChange}>
        Ειδοποιήσεις email
      </Switch>,
    );
    const toggle = screen.getByRole('switch', { name: 'Ειδοποιήσεις email' });
    await user.click(toggle);
    expect(onChangeAsync).toHaveBeenCalledWith(true);
    expect(toggle).toBeChecked();
    expect(toggle).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByRole('status')).toHaveTextContent('Αποθήκευση…');
    // Toggling is paused while saving.
    await user.click(toggle);
    expect(onChangeAsync).toHaveBeenCalledTimes(1);
    await act(async () => {
      resolve();
      await Promise.resolve();
    });
    expect(toggle).toBeChecked();
    expect(toggle).not.toHaveAttribute('aria-busy');
    expect(onChange).toHaveBeenCalledWith(true);
  });

  it('reverts and reports when the server rejects', async () => {
    const onError = vi.fn();
    const onChange = vi.fn();
    const failure = new Error('500');
    const { user } = renderWithDs(
      <Switch onChangeAsync={() => Promise.reject(failure)} onError={onError} onChange={onChange}>
        Ειδοποιήσεις SMS
      </Switch>,
    );
    const toggle = screen.getByRole('switch', { name: 'Ειδοποιήσεις SMS' });
    await user.click(toggle);
    await waitFor(() => {
      expect(onError).toHaveBeenCalledWith(failure);
    });
    expect(toggle).not.toBeChecked();
    expect(onChange).not.toHaveBeenCalled();
    await waitFor(() => {
      expect(document.getElementById('ds-live-assertive')).toHaveTextContent(
        'Η αλλαγή «Ειδοποιήσεις SMS» δεν αποθηκεύτηκε',
      );
    });
  });

  it('keeps a disabled-with-reason switch focusable and unchanged', async () => {
    const reason = 'Η ρύθμιση ορίζεται από τον διαχειριστή';
    const { user } = renderWithDs(
      <Switch disabledReason={reason} defaultSelected>
        Βοηθός ΤΝ
      </Switch>,
    );
    const toggle = screen.getByRole('switch', { name: 'Βοηθός ΤΝ' });
    expect(toggle).toHaveAttribute('aria-disabled', 'true');
    expect(toggle).toHaveAccessibleDescription(reason);
    await user.tab();
    expect(toggle).toHaveFocus();
    expect(await screen.findByRole('tooltip')).toHaveTextContent(reason);
    await user.keyboard(' ');
    expect(toggle).toBeChecked();
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
    });
  });

  it('removes a reasonless disabled switch from the tab order', () => {
    renderWithDs(<Switch isDisabled>Α</Switch>);
    expect(screen.getByRole('switch', { name: 'Α' })).toBeDisabled();
  });

  it('shows read-only state as text', () => {
    renderWithDs(
      <Switch isReadOnly defaultSelected>
        Εορτασμοί
      </Switch>,
    );
    expect(screen.queryByRole('switch')).not.toBeInTheDocument();
    expect(screen.getByText('Ενεργό')).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <Switch showStateText defaultSelected>
        Celebrations
      </Switch>,
    );
    expect(screen.getByText('On')).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <Switch showStateText>Εορτασμοί</Switch>
        <Switch disabledReason="Κλειδωμένο">Βοηθός ΤΝ</Switch>
        <Switch isPending defaultSelected>
          Συγχρονισμός
        </Switch>
        <Switch isReadOnly>Εικόνες φόντου</Switch>
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
