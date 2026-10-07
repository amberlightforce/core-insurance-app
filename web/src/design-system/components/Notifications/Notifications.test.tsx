import { act, screen, waitFor, within } from '@testing-library/react';
import { Clock } from 'lucide-react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import { NotificationBell, NotificationCenter, type NotificationItem } from './Notifications';

const items: NotificationItem[] = [
  {
    id: '1',
    title: 'Παραβίαση προθεσμίας προσφοράς',
    body: 'ΖΗΜ-2026-000091 · η προθεσμία 3 μηνών έληξε',
    time: 'πριν 5 λεπτά',
    unread: true,
    family: 'danger',
    icon: Clock,
    actions: <Button size="sm">Άνοιγμα</Button>,
  },
  { id: '2', title: 'Νέο έγγραφο', time: '14:02', unread: false },
];

describe('NotificationBell', () => {
  it('names the count with an ICU plural and caps the badge at 99+', () => {
    const { rerender } = renderWithDs(<NotificationBell count={1} />);
    expect(screen.getByRole('button', { name: 'Ειδοποιήσεις, 1 νέα' })).toBeInTheDocument();
    rerender(<NotificationBell count={3} attention />);
    const bell = screen.getByRole('button', { name: 'Ειδοποιήσεις, 3 νέες' });
    expect(bell.querySelector('[data-attention]')).toHaveTextContent('3');
    rerender(<NotificationBell count={140} />);
    expect(screen.getByRole('button', { name: 'Ειδοποιήσεις, 140 νέες' })).toHaveTextContent('99+');
    rerender(<NotificationBell count={0} />);
    expect(screen.getByRole('button', { name: 'Ειδοποιήσεις' })).toBeInTheDocument();
  });

  it('swings only when an urgent signal arrives', () => {
    const { rerender } = renderWithDs(<NotificationBell count={2} />);
    const bell = screen.getByRole('button');
    expect(bell.querySelector('[data-swing]')).toBeNull();
    rerender(<NotificationBell count={3} />);
    expect(bell.querySelector('[data-swing]')).toBeNull();
    rerender(<NotificationBell count={3} urgentSignal={1} />);
    expect(bell.querySelector('[data-swing]')).not.toBeNull();
  });

  it('is named in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<NotificationBell count={2} />);
    expect(screen.getByRole('button', { name: 'Notifications, 2 new' })).toBeInTheDocument();
  });
});

function Center({
  onSeen,
  onMarkAllRead,
}: {
  onSeen?: (id: string) => void;
  onMarkAllRead?: () => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      <NotificationBell
        count={1}
        attention
        isExpanded={open}
        onPress={() => {
          setOpen(true);
        }}
      />
      <NotificationCenter
        isOpen={open}
        onOpenChange={setOpen}
        notifications={items}
        {...(onSeen ? { onSeen } : {})}
        {...(onMarkAllRead ? { onMarkAllRead } : {})}
      />
    </>
  );
}

describe('NotificationCenter', () => {
  it('opens a 400 px non-modal drawer from the bell and closes with Esc', async () => {
    const { user } = renderWithDs(<Center />);
    const bell = screen.getByRole('button', { name: 'Ειδοποιήσεις, 1 νέα' });
    expect(bell).toHaveAttribute('aria-expanded', 'false');
    await user.click(bell);
    expect(bell).toHaveAttribute('aria-expanded', 'true');
    const panel = await screen.findByRole('complementary', { name: 'Ειδοποιήσεις' });
    expect(panel).toHaveAttribute('data-width', '400');
    expect(within(panel).getAllByRole('listitem')).toHaveLength(2);
    expect(
      within(panel).getByText('Παραβίαση προθεσμίας προσφοράς').parentElement,
    ).toHaveTextContent('Μη αναγνωσμένη');
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('complementary')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(bell).toHaveFocus();
    });
  });

  it('marks unread items seen after 1 s in view and offers mark-all-read', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const onSeen = vi.fn();
    const onMarkAllRead = vi.fn();
    const { user } = renderWithDs(<Center onSeen={onSeen} onMarkAllRead={onMarkAllRead} />);
    await user.click(screen.getByRole('button', { name: 'Ειδοποιήσεις, 1 νέα' }));
    const panel = await screen.findByRole('complementary');
    expect(onSeen).not.toHaveBeenCalled();
    act(() => {
      vi.advanceTimersByTime(1000);
    });
    expect(onSeen).toHaveBeenCalledWith('1');
    expect(panel.querySelector('[data-fading]')).not.toBeNull();
    await user.click(within(panel).getByRole('button', { name: 'Σήμανση όλων ως αναγνωσμένων' }));
    expect(onMarkAllRead).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });

  it('shows the empty state', () => {
    renderWithDs(<NotificationCenter isOpen onOpenChange={vi.fn()} notifications={[]} />);
    expect(screen.getByText('Δεν υπάρχουν ειδοποιήσεις')).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderWithDs(<NotificationCenter isOpen onOpenChange={vi.fn()} notifications={items} />);
    await expectNoA11yViolations(await screen.findByRole('complementary'));
  });
});
