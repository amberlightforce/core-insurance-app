import { act, screen } from '@testing-library/react';
import { Button as AriaButton } from 'react-aria-components';
import { describe, expect, it } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { formatShortcut, matchesShortcut } from '../Kbd';
import { Tooltip } from './Tooltip';

describe('Tooltip', () => {
  it('opens immediately on keyboard focus, describes the trigger and closes with Esc without moving focus', async () => {
    const { user } = renderWithDs(
      <Tooltip content="Αντιγραφή αριθμού">
        <AriaButton>Αντιγραφή</AriaButton>
      </Tooltip>,
    );
    await user.tab();
    const trigger = screen.getByRole('button', { name: 'Αντιγραφή' });
    expect(trigger).toHaveFocus();
    const tooltip = await screen.findByRole('tooltip');
    expect(tooltip).toHaveTextContent('Αντιγραφή αριθμού');
    expect(trigger).toHaveAttribute('aria-describedby', tooltip.id);

    await user.keyboard('{Escape}');
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
    expect(trigger).toHaveFocus();
  });

  it('shows a shortcut chip', async () => {
    const { user } = renderWithDs(
      <Tooltip content="Αναζήτηση" shortcut="Mod+K">
        <AriaButton>Α</AriaButton>
      </Tooltip>,
    );
    await user.tab();
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Ctrl');
  });

  it('stays closed when disabled', async () => {
    const { user } = renderWithDs(
      <Tooltip content="κείμενο" isDisabled>
        <AriaButton>Α</AriaButton>
      </Tooltip>,
    );
    await user.tab();
    await act(() => new Promise((r) => setTimeout(r, 20)));
    expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
  });

  it('has no axe violations when open', async () => {
    const { container } = renderWithDs(
      <Tooltip content="Αντιγραφή αριθμού" isOpen>
        <AriaButton>Αντιγραφή</AriaButton>
      </Tooltip>,
    );
    await screen.findByRole('tooltip');
    await expectNoA11yViolations(container);
  });
});

describe('shortcuts', () => {
  it('formats Mod per platform', () => {
    expect(formatShortcut('Mod+K', false)).toEqual(['Ctrl', 'K']);
    expect(formatShortcut('Mod+Enter', true)).toEqual(['⌘', '↵']);
  });

  it('matches key events', () => {
    const event = new KeyboardEvent('keydown', { key: 'k', ctrlKey: true });
    expect(matchesShortcut(event, 'Mod+K')).toBe(true);
    expect(matchesShortcut(new KeyboardEvent('keydown', { key: 'k' }), 'Mod+K')).toBe(false);
    expect(
      matchesShortcut(
        new KeyboardEvent('keydown', { key: 'K', ctrlKey: true, shiftKey: true }),
        'Mod+Shift+K',
      ),
    ).toBe(true);
  });
});
