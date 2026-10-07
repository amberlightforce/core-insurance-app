import { fireEvent, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { shouldStack, widthForKey } from './splitMath';
import { SplitView } from './SplitView';

function renderSplit(isDetailOpen = false, onCloseDetail = vi.fn()) {
  return renderWithDs(
    <SplitView
      listLabel="Οι παραπομπές μου"
      detailLabel="Λεπτομέρειες παραπομπής"
      list={<p>λίστα</p>}
      detail={<p>λεπτομέρειες</p>}
      isDetailOpen={isDetailOpen}
      onCloseDetail={onCloseDetail}
    />,
  );
}

describe('SplitView', () => {
  it('exposes a keyboard-operable separator with its value', async () => {
    const { user } = renderSplit();
    const separator = screen.getByRole('separator', { name: 'Αλλαγή πλάτους λίστας' });
    expect(separator).toHaveAttribute('aria-valuenow', '420');
    expect(separator).toHaveAttribute('aria-valuemin', '320');
    expect(separator).toHaveAttribute('aria-valuemax', '640');

    separator.focus();
    await user.keyboard('{ArrowRight}');
    expect(separator).toHaveAttribute('aria-valuenow', '436');
    await user.keyboard('{Shift>}{ArrowLeft}{/Shift}');
    expect(separator).toHaveAttribute('aria-valuenow', '372');
    await user.keyboard('{End}');
    expect(separator).toHaveAttribute('aria-valuenow', '640');
    await user.keyboard('{Home}');
    expect(separator).toHaveAttribute('aria-valuenow', '320');
  });

  it('names both panes as regions', () => {
    renderSplit();
    expect(screen.getByRole('region', { name: 'Οι παραπομπές μου' })).toBeInTheDocument();
    expect(screen.getByRole('region', { name: 'Λεπτομέρειες παραπομπής' })).toBeInTheDocument();
  });

  it('uses the comfortable default width', () => {
    renderWithDs(
      <SplitView
        listLabel="Λίστα"
        detailLabel="Λεπτομέρειες"
        list={null}
        detail={null}
        isDetailOpen={false}
        onCloseDetail={vi.fn()}
      />,
      { preferences: { density: 'comfortable' } },
    );
    expect(screen.getByRole('separator')).toHaveAttribute('aria-valuenow', '460');
  });

  it('has no axe violations', async () => {
    const { container } = renderSplit();
    await expectNoA11yViolations(container);
  });
});

describe('split math', () => {
  it('stacks when the detail would be narrower than 560 px', () => {
    expect(shouldStack(900, 420)).toBe(true);
    expect(shouldStack(1000, 420)).toBe(false);
    expect(shouldStack(0, 420)).toBe(false);
  });

  it('maps keys to widths', () => {
    expect(widthForKey('ArrowLeft', false, 330)).toBe(320);
    expect(widthForKey('ArrowRight', true, 600)).toBe(640);
    expect(widthForKey('a', false, 400)).toBeNull();
  });
});

describe('SplitView stacked (narrow container)', () => {
  const RealObserver = globalThis.ResizeObserver;
  beforeEach(() => {
    globalThis.ResizeObserver = class {
      constructor(private readonly callback: ResizeObserverCallback) {}
      observe() {
        this.callback([{ contentRect: { width: 700 } } as ResizeObserverEntry], this);
      }
      unobserve() {
        return undefined;
      }
      disconnect() {
        return undefined;
      }
    };
  });
  afterEach(() => {
    globalThis.ResizeObserver = RealObserver;
  });

  it('shows only the list until an item opens', () => {
    renderSplit(false);
    expect(screen.queryByRole('separator')).not.toBeInTheDocument();
    expect(screen.getByText('λίστα')).toBeVisible();
    expect(screen.getByText('λεπτομέρειες')).not.toBeVisible();
  });

  it('covers the list with the detail and goes back with the button, Esc or Alt+←', async () => {
    const onClose = vi.fn();
    const { user } = renderSplit(true, onClose);
    expect(screen.getByText('λίστα')).not.toBeVisible();
    await user.click(screen.getByRole('button', { name: /Πίσω στη λίστα/ }));
    fireEvent.keyDown(screen.getByText('λεπτομέρειες'), { key: 'Escape' });
    fireEvent.keyDown(screen.getByText('λεπτομέρειες'), { key: 'ArrowLeft', altKey: true });
    expect(onClose).toHaveBeenCalledTimes(3);
  });
});
