import { act, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Kbd } from '../Kbd';
import { Spinner } from './Spinner';

describe('Spinner', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('appears only after 400 ms so fast work never flashes', () => {
    renderWithDs(<Spinner />);
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
    act(() => {
      vi.advanceTimersByTime(400);
    });
    expect(screen.getByRole('img', { name: 'Φόρτωση…' })).toBeInTheDocument();
  });

  it('can render immediately, unlabelled when a parent announces the state', () => {
    const { container } = renderWithDs(<Spinner delayed={false} label={null} />);
    const svg = container.querySelector('svg');
    expect(svg).toHaveAttribute('aria-hidden', 'true');
  });

  it('is labelled in English', async () => {
    await act(() => i18n.changeLanguage('en'));
    renderWithDs(<Spinner delayed={false} />);
    expect(screen.getByRole('img', { name: 'Loading…' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    vi.useRealTimers();
    const { container } = renderWithDs(
      <div>
        <Spinner delayed={false} />
        <Kbd shortcut="Mod+Enter" />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
