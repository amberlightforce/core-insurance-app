import { act, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  OfflineState,
  PermissionDenied,
  PermissionLimited,
} from './index';

afterEach(() => {
  vi.useRealTimers();
});

describe('EmptyState', () => {
  it('renders the first-use pattern with a decorative illustration, action and help link', () => {
    const { container } = renderWithDs(
      <EmptyState
        kind="first-use"
        headline="Δεν έχετε ακόμη αποθηκευμένες προβολές"
        description="Αποθηκεύστε φίλτρα και στήλες για να επιστρέφετε με ένα κλικ."
        action={<Button variant="primary">Δημιουργία προβολής</Button>}
        helpHref="/help/views"
      />,
    );
    expect(
      screen.getByRole('heading', { level: 2, name: 'Δεν έχετε ακόμη αποθηκευμένες προβολές' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Δημιουργία προβολής' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Μάθετε περισσότερα' })).toHaveAttribute(
      'href',
      '/help/views',
    );
    const svg = container.querySelector('[data-illustration="ILL-01"]');
    expect(svg).toHaveAttribute('aria-hidden', 'true');
  });

  it('renders filtered-empty without an illustration and clears filters', async () => {
    const onClear = vi.fn();
    const { container, user } = renderWithDs(
      <EmptyState
        kind="filtered"
        filters={[
          { label: 'Κατάσταση', value: 'Ακυρώθηκε' },
          { label: 'Παραγωγός', value: '10233' },
        ]}
        onClearFilters={onClear}
      />,
    );
    expect(screen.getByRole('status')).toHaveTextContent(
      'Κανένα αποτέλεσμα για: Κατάσταση = Ακυρώθηκε, Παραγωγός = 10233',
    );
    expect(container.querySelector('[data-illustration]')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Εκκαθάριση φίλτρων' }));
    expect(onClear).toHaveBeenCalledOnce();
  });

  it('renders done-empty with the Laurel illustration', () => {
    const { container } = renderWithDs(<EmptyState kind="done" />);
    expect(container.querySelector('[data-illustration="ILL-03"]')).not.toBeNull();
    expect(screen.getByRole('status')).toHaveTextContent('Καλή δουλειά');
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<EmptyState kind="filtered" filters={[{ label: 'Status', value: 'Lapsed' }]} />);
    expect(screen.getByRole('status')).toHaveTextContent('No results for: Status = Lapsed');
  });
});

describe('LoadingState', () => {
  it('is busy with a hidden label and shows skeletons only after 150 ms', () => {
    vi.useFakeTimers();
    const { container } = renderWithDs(<LoadingState />);
    const region = container.querySelector('[aria-busy="true"]');
    expect(region).not.toBeNull();
    expect(region).toHaveTextContent('Φόρτωση…');
    expect(container.querySelectorAll('[data-shape]')).toHaveLength(0);
    act(() => {
      vi.advanceTimersByTime(160);
    });
    expect(container.querySelectorAll('[data-shape]')).toHaveLength(3);
  });

  it('switches to the long state with stage text after 2 s', () => {
    vi.useFakeTimers();
    renderWithDs(
      <LoadingState
        stage={{ current: 2, total: 4, label: 'Δημιουργία αναφοράς…' }}
        estimate="περίπου 1 λεπτό"
      />,
    );
    expect(screen.queryByRole('progressbar')).toBeNull();
    act(() => {
      vi.advanceTimersByTime(2100);
    });
    const bar = screen.getByRole('progressbar');
    expect(bar).toHaveAttribute('aria-valuenow', '2');
    expect(bar).toHaveAttribute('aria-valuemax', '4');
    expect(bar).toHaveAttribute('aria-valuetext', 'Δημιουργία αναφοράς… 2 από 4 στάδια');
    expect(screen.getByText('Εκτιμώμενος χρόνος: περίπου 1 λεπτό')).toBeInTheDocument();
  });
});

describe('ErrorState', () => {
  it('shows a recoverable region banner with retry and the correlation id behind a disclosure', async () => {
    const onRetry = vi.fn();
    const { user } = renderWithDs(
      <ErrorState
        message="Δεν ήταν δυνατή η φόρτωση των πληρωμών."
        onRetry={onRetry}
        correlationId="4f2a91c0"
      />,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('Δεν ήταν δυνατή η φόρτωση των πληρωμών.');
    const details = screen.getByRole('button', { name: 'Λεπτομέρειες' });
    expect(details).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByText('Κωδικός: 4f2a91c0')).not.toBeVisible();
    await user.click(details);
    expect(details).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Κωδικός: 4f2a91c0')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Επανάληψη' }));
    expect(onRetry).toHaveBeenCalledOnce();
  });

  it('shows the page-level error with home and report actions', async () => {
    const onReport = vi.fn();
    const { user } = renderWithDs(
      <ErrorState scope="page" homeHref="/" onReport={onReport} correlationId="trace-1" />,
    );
    expect(
      screen.getByRole('heading', { level: 1, name: 'Κάτι πήγε στραβά από τη δική μας πλευρά' }),
    ).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Επιστροφή στην αρχική/ })).toHaveAttribute(
      'href',
      '/',
    );
    await user.click(screen.getByRole('button', { name: 'Αναφορά προβλήματος' }));
    expect(onReport).toHaveBeenCalledWith('trace-1');
  });
});

describe('PermissionDenied', () => {
  it('names what is restricted and who can grant it, and sends an access request', async () => {
    const onRequest = vi.fn(() => Promise.resolve());
    const { user, container } = renderWithDs(
      <PermissionDenied
        restriction="Δεν έχετε πρόσβαση στις αναφορές Solvency II."
        grantor="ο υπεύθυνος ρόλων της Οικονομικής Διεύθυνσης"
        onRequestAccess={onRequest}
      />,
    );
    expect(screen.getByText('Δεν έχετε πρόσβαση στις αναφορές Solvency II.')).toBeInTheDocument();
    expect(screen.getByText(/ο υπεύθυνος ρόλων/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Αίτημα πρόσβασης' }));
    expect(onRequest).toHaveBeenCalledOnce();
    expect(await within(container).findByRole('status')).toHaveTextContent(
      'Το αίτημα πρόσβασης στάλθηκε',
    );
  });
});

describe('PermissionLimited and OfflineState', () => {
  it('masks values with a «κρυφό» name and a caption, never blank', () => {
    renderWithDs(<PermissionLimited maskedValue="•••• 4471" />);
    expect(screen.getByText('κρυφό')).toBeInTheDocument();
    expect(
      screen.getByText('Απαιτείται δικαίωμα προβολής ευαίσθητων δεδομένων'),
    ).toBeInTheDocument();
  });

  it('shows the offline banner as a status', () => {
    const { container } = renderWithDs(<OfflineState lastSynced="14:32" />);
    const status = within(container).getByRole('status');
    expect(status).toHaveTextContent('Εκτός σύνδεσης');
    expect(status).toHaveTextContent('Τελευταίος συγχρονισμός 14:32');
  });
});

describe('States accessibility', () => {
  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <EmptyState kind="first-use" headline="Καμία προβολή" helpHref="/help" />
        <EmptyState kind="filtered" filters={[{ label: 'Κατάσταση', value: 'Ενεργό' }]} />
        <LoadingState immediate />
        <ErrorState onRetry={() => undefined} correlationId="abc" />
        <PermissionDenied restriction="Δεν έχετε πρόσβαση." headingLevel={2} />
        <PermissionLimited maskedValue="•••• 4471" />
        <OfflineState variant="page" />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
