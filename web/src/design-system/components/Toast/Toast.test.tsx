import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { clearAnnouncements } from '../../a11y/announce';
import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import { Toaster } from './Toast';
import { durationFor, toast } from './toastStore';

afterEach(() => {
  act(() => {
    toast.clear();
  });
  clearAnnouncements();
  vi.useRealTimers();
});

describe('toast durations', () => {
  it('follows the guide: 4 / 6 with action / 5 / 8 s, errors persist', () => {
    expect(durationFor('success', false)).toBe(4000);
    expect(durationFor('success', true)).toBe(6000);
    expect(durationFor('info', false)).toBe(5000);
    expect(durationFor('warning', false)).toBe(8000);
    expect(durationFor('error', true)).toBeNull();
  });
});

describe('Toaster', () => {
  it('renders a labelled region with polite success and alert errors', async () => {
    renderWithDs(<Toaster />);
    const region = screen.getByRole('region', { name: 'Ειδοποιήσεις' });
    act(() => {
      toast.success({ title: 'Αποθηκεύτηκε' });
      toast.error({
        title: 'Η πληρωμή δεν στάλθηκε',
        description: 'Τα στοιχεία σας αποθηκεύτηκαν.',
      });
    });
    expect(within(region).getByText('Αποθηκεύτηκε')).toBeInTheDocument();
    expect(within(region).getByRole('alert')).toHaveTextContent('Η πληρωμή δεν στάλθηκε');
    await waitFor(() => {
      expect(document.getElementById('ds-live-polite')).toHaveTextContent('Αποθηκεύτηκε');
    });
  });

  it('auto-dismisses success after 4 s and pauses while hovered', () => {
    vi.useFakeTimers();
    renderWithDs(<Toaster />);
    act(() => {
      toast.success({ title: 'Αποθηκεύτηκε' });
    });
    const region = screen.getByRole('region');
    fireEvent.pointerEnter(region);
    act(() => {
      vi.advanceTimersByTime(10000);
    });
    expect(within(region).getByText('Αποθηκεύτηκε')).toBeInTheDocument();
    fireEvent.pointerLeave(region);
    act(() => {
      vi.advanceTimersByTime(4000);
    });
    act(() => {
      vi.advanceTimersByTime(300);
    });
    expect(within(region).queryByText('Αποθηκεύτηκε')).not.toBeInTheDocument();
  });

  it('pauses while the window is blurred and keeps errors until dismissed', () => {
    vi.useFakeTimers();
    renderWithDs(<Toaster />);
    act(() => {
      toast.info({ title: 'Η ανανέωση ξεκίνησε' });
      toast.error({ title: 'Αποτυχία αποστολής' });
    });
    act(() => {
      window.dispatchEvent(new Event('blur'));
    });
    act(() => {
      vi.advanceTimersByTime(20000);
    });
    expect(within(screen.getByRole('region')).getByText('Η ανανέωση ξεκίνησε')).toBeInTheDocument();
    act(() => {
      window.dispatchEvent(new Event('focus'));
    });
    act(() => {
      vi.advanceTimersByTime(5000);
    });
    act(() => {
      vi.advanceTimersByTime(300);
    });
    expect(
      within(screen.getByRole('region')).queryByText('Η ανανέωση ξεκίνησε'),
    ).not.toBeInTheDocument();
    expect(screen.getByText('Αποτυχία αποστολής')).toBeInTheDocument();
  });

  it('shows three at a time and queues the fourth', () => {
    renderWithDs(<Toaster />);
    act(() => {
      for (const n of [1, 2, 3, 4]) toast.error({ title: `Σφάλμα ${String(n)}` });
    });
    const items = within(screen.getByRole('region')).getAllByRole('listitem');
    expect(items).toHaveLength(3);
    expect(items.map((item) => item.getAttribute('data-depth'))).toEqual(['2', '1', '0']);
    expect(screen.queryByText('Σφάλμα 4')).not.toBeInTheDocument();
    act(() => {
      toast.clear();
      toast.error({ title: 'Σφάλμα 4' });
    });
    expect(screen.getByText('Σφάλμα 4')).toBeInTheDocument();
  });

  it('runs the action (Αναίρεση) and dismisses', async () => {
    const onUndo = vi.fn();
    const { user } = renderWithDs(<Toaster />);
    act(() => {
      toast.success({
        title: 'Η εργασία ανατέθηκε',
        action: { label: 'Αναίρεση', onAction: onUndo },
      });
    });
    await user.click(screen.getByRole('button', { name: 'Αναίρεση' }));
    expect(onUndo).toHaveBeenCalledTimes(1);
    await waitFor(() => {
      expect(
        within(screen.getByRole('region')).queryByText('Η εργασία ανατέθηκε'),
      ).not.toBeInTheDocument();
    });
  });

  it('F8 focuses the newest toast and Esc dismisses it, returning focus', async () => {
    const { user } = renderWithDs(
      <>
        <Button>Εργασία</Button>
        <Toaster />
      </>,
    );
    act(() => {
      toast.error({ title: 'Πρώτο' });
      toast.warning({ title: 'Δεύτερο' });
    });
    const origin = screen.getByRole('button', { name: 'Εργασία' });
    await user.click(origin);
    await user.keyboard('{F8}');
    const items = within(screen.getByRole('region')).getAllByRole('listitem');
    expect(items[1]).toHaveFocus();
    expect(items[1]).toHaveTextContent('Δεύτερο');

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByText('Δεύτερο')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(within(screen.getByRole('region')).getByRole('listitem')).toHaveFocus();
    });
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(origin).toHaveFocus();
    });
  });

  it('resolves a progress toast into success', async () => {
    renderWithDs(<Toaster />);
    let handle: ReturnType<typeof toast.progress> | undefined;
    act(() => {
      handle = toast.progress({ title: 'Έκδοση 48 ασφαλιστηρίων', value: 0.25 });
    });
    expect(screen.getByRole('progressbar', { name: 'Έκδοση 48 ασφαλιστηρίων' })).toHaveAttribute(
      'aria-valuenow',
      '0.25',
    );
    act(() => {
      handle?.update({ value: 0.5 });
    });
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '0.5');
    act(() => {
      handle?.success({
        title: 'Εκδόθηκαν 48 ασφαλιστήρια · 2 απέτυχαν',
        action: { label: 'Προβολή', onAction: vi.fn() },
      });
    });
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(
      within(screen.getByRole('region')).getByText('Εκδόθηκαν 48 ασφαλιστήρια · 2 απέτυχαν'),
    ).toBeInTheDocument();
    await waitFor(() => {
      expect(document.getElementById('ds-live-polite')).toHaveTextContent('Εκδόθηκαν 48');
    });
  });

  it('labels the region in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Toaster />);
    act(() => {
      toast.success({ title: 'Saved' });
    });
    expect(screen.getByRole('region', { name: 'Notifications' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Dismiss notification' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderWithDs(<Toaster />);
    act(() => {
      toast.success({ title: 'Αποθηκεύτηκε', action: { label: 'Αναίρεση', onAction: vi.fn() } });
      toast.error({ title: 'Αποτυχία' });
    });
    await expectNoA11yViolations(screen.getByRole('region'));
  });
});
