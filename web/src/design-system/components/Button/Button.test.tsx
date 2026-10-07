import { act, renderHook, screen } from '@testing-library/react';
import { Check, Search } from 'lucide-react';
import { describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from './Button';
import { useSubmitGuard } from './useSubmitGuard';

describe('Button', () => {
  it('is a native button activated by Enter and Space', async () => {
    const onPress = vi.fn();
    const { user } = renderWithDs(<Button onPress={onPress}>Αποθήκευση</Button>);
    const button = screen.getByRole('button', { name: 'Αποθήκευση' });
    expect(button.tagName).toBe('BUTTON');

    await user.tab();
    expect(button).toHaveFocus();
    await user.keyboard('{Enter}');
    await user.keyboard(' ');
    expect(onPress).toHaveBeenCalledTimes(2);
  });

  it('renders every variant with its data attribute', () => {
    const variants = [
      'primary',
      'commit',
      'secondary',
      'ghost',
      'danger',
      'danger-ghost',
      'link',
      'ai',
    ] as const;
    renderWithDs(
      <div>
        {variants.map((v) => (
          <Button key={v} variant={v}>
            {v}
          </Button>
        ))}
      </div>,
    );
    for (const v of variants) {
      expect(screen.getByRole('button', { name: v })).toHaveAttribute('data-variant', v);
    }
  });

  it('keeps a disabled-with-reason button focusable, describes the reason and swallows presses', async () => {
    const onPress = vi.fn();
    const reason = 'Δεν μπορεί να γίνει δέσμευση: 2 ανοιχτά ζητήματα ανάληψης';
    const { user } = renderWithDs(
      <Button variant="commit" disabledReason={reason} onPress={onPress}>
        Δέσμευση
      </Button>,
    );
    const button = screen.getByRole('button', { name: 'Δέσμευση' });
    expect(button).toHaveAttribute('aria-disabled', 'true');
    expect(button).not.toBeDisabled();
    expect(button).toHaveAccessibleDescription(reason);

    await user.tab();
    expect(button).toHaveFocus();
    // Keyboard focus opens the reason tooltip immediately.
    expect(await screen.findByRole('tooltip')).toHaveTextContent(reason);
    await user.keyboard('{Enter}');
    await user.click(button);
    expect(onPress).not.toHaveBeenCalled();
  });

  it('removes a reasonless disabled button from the tab order', async () => {
    const { user } = renderWithDs(
      <>
        <Button isDisabled>Α</Button>
        <Button>Β</Button>
      </>,
    );
    await user.tab();
    expect(screen.getByRole('button', { name: 'Β' })).toHaveFocus();
  });

  it('shows a pending state that keeps the name and ignores presses', async () => {
    const onPress = vi.fn();
    const { user } = renderWithDs(
      <Button variant="primary" isLoading onPress={onPress}>
        Υποβολή
      </Button>,
    );
    const button = screen.getByRole('button', { name: /Υποβολή/ });
    expect(button).toHaveAttribute('data-pending', 'true');
    expect(button).toHaveAttribute('aria-disabled', 'true');
    await user.click(button);
    expect(onPress).not.toHaveBeenCalled();
  });

  it('labels icon-only buttons and shows the label as a tooltip on focus', async () => {
    const { user } = renderWithDs(
      <Button icon={Search} label="Αναζήτηση" variant="ghost" shortcut="Mod+K" />,
    );
    const button = screen.getByRole('button', { name: 'Αναζήτηση' });
    await user.tab();
    expect(button).toHaveFocus();
    const tooltip = await screen.findByRole('tooltip');
    expect(tooltip).toHaveTextContent('Αναζήτηση');
    expect(tooltip).toHaveTextContent('Ctrl');
  });

  it('exposes aria-pressed for toggles', () => {
    renderWithDs(
      <Button isPressed icon={Check}>
        Επιλογή
      </Button>,
    );
    expect(screen.getByRole('button', { name: 'Επιλογή' })).toHaveAttribute('aria-pressed', 'true');
  });

  it('shows the shortcut chip inside large buttons', () => {
    renderWithDs(
      <Button variant="commit" size="lg" shortcut="Mod+Enter">
        Έγκριση
      </Button>,
    );
    expect(screen.getByRole('button', { name: /Έγκριση/ })).toHaveTextContent('↵');
  });

  it('marks the MI-67 sweep when a commit button becomes enabled', () => {
    const { rerender } = renderWithDs(
      <Button variant="commit" disabledReason="1 ζήτημα">
        Έκδοση
      </Button>,
    );
    rerender(<Button variant="commit">Έκδοση</Button>);
    expect(screen.getByRole('button', { name: 'Έκδοση' })).toHaveAttribute('data-sweep', 'true');
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <Button variant="primary">Αποθήκευση</Button>
        <Button variant="commit" disabledReason="Λείπει η ημερομηνία">
          Έκδοση
        </Button>
        <Button icon={Search} label="Αναζήτηση" />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});

describe('useSubmitGuard', () => {
  it('ignores repeat submits and reuses the idempotency key on retry', async () => {
    const { result } = renderHook(() => useSubmitGuard());
    const keys: string[] = [];
    let resolveFirst: () => void = () => undefined;
    const first = new Promise<void>((resolve) => {
      resolveFirst = resolve;
    });

    let p1: Promise<void> = Promise.resolve();
    act(() => {
      p1 = result.current.run(async (key) => {
        keys.push(key);
        await first;
        throw new Error('network');
      });
    });
    await act(() => result.current.run((key) => Promise.resolve(keys.push(key))));
    expect(keys).toHaveLength(1);

    resolveFirst();
    await act(async () => {
      await p1.catch(() => undefined);
    });
    await act(() => result.current.run((key) => Promise.resolve(keys.push(key))));
    expect(keys).toHaveLength(2);
    expect(keys[1]).toBe(keys[0]);
  });
});
