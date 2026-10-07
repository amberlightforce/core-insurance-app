import { act, renderHook, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Skeleton, SkeletonRegion, SkeletonText } from './Skeleton';
import { useDelayedLoading } from './useDelayedLoading';

describe('useDelayedLoading', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('shows nothing for loads shorter than 150 ms', () => {
    const { result, rerender } = renderHook(({ loading }) => useDelayedLoading(loading), {
      initialProps: { loading: true },
    });
    act(() => {
      vi.advanceTimersByTime(100);
    });
    expect(result.current).toBe(false);
    rerender({ loading: false });
    act(() => {
      vi.advanceTimersByTime(500);
    });
    expect(result.current).toBe(false);
  });

  it('appears after 150 ms and stays at least 300 ms', () => {
    const { result, rerender } = renderHook(({ loading }) => useDelayedLoading(loading), {
      initialProps: { loading: true },
    });
    act(() => {
      vi.advanceTimersByTime(150);
    });
    expect(result.current).toBe(true);
    rerender({ loading: false });
    act(() => {
      vi.advanceTimersByTime(200);
    });
    expect(result.current).toBe(true);
    act(() => {
      vi.advanceTimersByTime(100);
    });
    expect(result.current).toBe(false);
  });
});

describe('Skeleton', () => {
  it('is decorative', () => {
    const { container } = renderWithDs(
      <div>
        <Skeleton width="60%" />
        <SkeletonText lines={2} />
      </div>,
    );
    expect(container.querySelectorAll('[aria-hidden="true"]').length).toBeGreaterThan(0);
  });

  it('marks the region busy with a hidden «Φόρτωση…» and then shows the content', () => {
    vi.useFakeTimers();
    const { rerender, container } = renderWithDs(
      <SkeletonRegion isLoading fallback={<SkeletonText />}>
        <p>Περιεχόμενο</p>
      </SkeletonRegion>,
    );
    const region = container.firstElementChild;
    expect(region).toHaveAttribute('aria-busy', 'true');
    expect(screen.getByText('Φόρτωση…')).toHaveClass('ds-visually-hidden');
    expect(screen.queryByText('Περιεχόμενο')).not.toBeInTheDocument();
    act(() => {
      vi.advanceTimersByTime(150);
    });
    rerender(
      <SkeletonRegion isLoading={false} fallback={<SkeletonText />}>
        <p>Περιεχόμενο</p>
      </SkeletonRegion>,
    );
    act(() => {
      vi.advanceTimersByTime(300);
    });
    expect(screen.getByText('Περιεχόμενο')).toBeInTheDocument();
    expect(region).not.toHaveAttribute('aria-busy');
    vi.useRealTimers();
  });

  it('uses the English busy text', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <SkeletonRegion isLoading fallback={<SkeletonText />}>
        <p>Content</p>
      </SkeletonRegion>,
    );
    expect(screen.getByText('Loading…')).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <SkeletonRegion isLoading fallback={<SkeletonText />}>
        <p>Περιεχόμενο</p>
      </SkeletonRegion>,
    );
    await expectNoA11yViolations(container);
  });
});
