import { screen, within } from '@testing-library/react';
import { FileCheck } from 'lucide-react';
import { describe, expect, it, vi } from 'vitest';

import { formatDateTime } from '../../../format/dates';
import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Timeline, type TimelineEvent } from './index';
import { groupTimeline, relativeTime } from './timelineModel';

const now = new Date(2026, 9, 7, 14, 30);
const maria = { kind: 'user' as const, id: 'u1', name: 'Μαρία Παπαδοπούλου' };

const events: TimelineEvent[] = [
  {
    id: 'e1',
    at: new Date(2026, 9, 7, 14, 25),
    actor: maria,
    action: 'άλλαξε',
    object: { label: 'ΑΣΦ-2026-0412', href: '/policies/0412' },
    category: 'fields',
    diffs: [{ field: 'Ημερομηνία λήξης', from: '31/12/2026', to: '30/06/2027' }],
  },
  {
    id: 'e2',
    at: new Date(2026, 9, 7, 9, 0),
    actor: { kind: 'system' },
    action: 'εξέδωσε',
    object: { label: 'Ειδοποίηση ανανέωσης' },
    category: 'documents',
    icon: FileCheck,
    family: 'success',
  },
  {
    id: 'e3',
    at: new Date(2026, 9, 6, 16, 0),
    actor: { kind: 'agent', onBehalfOf: 'Μ. Π.' },
    action: 'πρότεινε απόθεμα',
    category: 'ai',
  },
  {
    id: 'e4',
    at: new Date(2026, 9, 5, 11, 0),
    actor: { kind: 'user', id: 'u2', name: 'Κώστας Νικολάου' },
    action: 'κατέγραψε πληρωμή',
    category: 'transactions',
  },
];

describe('timeline model', () => {
  it('collapses same-actor field edits within 5 minutes', () => {
    const runs: TimelineEvent[] = [0, 1, 2].map((i) => ({
      id: `r${String(i)}`,
      at: new Date(2026, 9, 7, 10, 10 - i * 2),
      actor: maria,
      action: 'άλλαξε',
      category: 'fields',
    }));
    const days = groupTimeline([
      ...runs,
      { ...runs[0], id: 'late', at: new Date(2026, 9, 7, 9, 0) } as TimelineEvent,
    ]);
    expect(days).toHaveLength(1);
    expect(days[0]?.entries).toHaveLength(2);
    expect(days[0]?.entries[0]?.type).toBe('group');
  });

  it('formats relative times in Greek', () => {
    expect(relativeTime(new Date(2026, 9, 7, 14, 25), now.getTime(), 'el', 'el-GR')).toBe(
      'πριν από 5 λεπτά',
    );
  });
});

describe('Timeline', () => {
  it('groups events in a list per day under real day headings (D-FE-19)', () => {
    renderWithDs(<Timeline aria-label="Ιστορικό" events={events} now={now} />);
    const region = screen.getByRole('region', { name: 'Ιστορικό' });
    expect(within(region).queryByRole('feed')).not.toBeInTheDocument();
    const headings = within(region).getAllByRole('heading', { level: 3 });
    expect(headings.map((h) => h.textContent)).toEqual(['Σήμερα', 'Χθες', 'Δευτέρα 5 Οκτωβρίου']);
    expect(within(region).getAllByRole('list')).toHaveLength(3);
    expect(within(region).getAllByRole('listitem')).toHaveLength(4);
  });

  it('uses the requested heading level', () => {
    renderWithDs(<Timeline aria-label="Ιστορικό" events={events} now={now} headingLevel={4} />);
    expect(screen.getAllByRole('heading', { level: 4 })).toHaveLength(3);
  });

  it('names actors, links objects and announces diffs as «από … σε …»', () => {
    renderWithDs(<Timeline aria-label="Ιστορικό" events={events} now={now} />);
    const items = screen.getAllByRole('listitem');
    expect(items[0]).toHaveTextContent(/Μαρία Παπαδοπούλου.*ΑΣΦ-2026-0412/);
    expect(
      within(items[0] ?? document.body).getByRole('link', { name: 'ΑΣΦ-2026-0412' }),
    ).toHaveAttribute('href', '/policies/0412');
    expect(screen.getByText('Ημερομηνία λήξης: από 31/12/2026 σε 30/06/2027')).toBeInTheDocument();
    expect(screen.getByText('πριν από 5 λεπτά')).toBeInTheDocument();
    // Absolute times are Europe/Athens (staff time), whatever the machine's zone.
    expect(
      screen.getAllByText(formatDateTime(new Date(2026, 9, 7, 14, 25))).length,
    ).toBeGreaterThan(0);
    expect(screen.getByText('Σύστημα')).toBeInTheDocument();
    expect(screen.getByText('Πράκτορας ΤΝ για Μ. Π.')).toBeInTheDocument();
  });

  it('filters by type with a radio group and shows the filtered empty state', async () => {
    const onFilterChange = vi.fn();
    const { user } = renderWithDs(
      <Timeline
        aria-label="Ιστορικό"
        events={events.filter((e) => e.category !== 'communication')}
        now={now}
        onFilterChange={onFilterChange}
      />,
    );
    const group = screen.getByRole('radiogroup', { name: 'Τύπος συμβάντος' });
    const all = within(group).getByRole('radio', { name: 'Όλα' });
    expect(all).toBeChecked();
    await user.click(within(group).getByRole('radio', { name: 'Έγγραφα' }));
    expect(onFilterChange).toHaveBeenCalledWith('documents');
    expect(screen.getAllByRole('listitem')).toHaveLength(1);
    await user.keyboard('{ArrowRight}');
    expect(within(group).getByRole('radio', { name: 'Επικοινωνία' })).toBeChecked();
    expect(screen.getByText('Δεν υπάρχουν συμβάντα με αυτά τα φίλτρα')).toBeInTheDocument();
  });

  it('expands collapsed edits', async () => {
    const runs: TimelineEvent[] = [0, 1, 2].map((i) => ({
      id: `r${String(i)}`,
      at: new Date(2026, 9, 7, 10, 10 - i),
      actor: maria,
      action: 'άλλαξε',
      category: 'fields',
      diffs: [{ field: `Πεδίο ${String(i)}`, from: 'α', to: 'β' }],
    }));
    const { user } = renderWithDs(<Timeline aria-label="Ιστορικό" events={runs} now={now} />);
    const article = screen.getAllByRole('listitem')[0] ?? document.body;
    expect(article).toHaveTextContent(/έκανε 3 αλλαγές/);
    const toggle = within(article).getByRole('button', { name: 'Εμφάνιση αλλαγών' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await user.click(toggle);
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Πεδίο 1: από α σε β')).toBeVisible();
  });

  it('loads older events and is busy meanwhile', async () => {
    const onLoadOlder = vi.fn();
    const { user, rerender } = renderWithDs(
      <Timeline
        aria-label="Ιστορικό"
        events={events}
        now={now}
        hasMore
        onLoadOlder={onLoadOlder}
      />,
    );
    await user.click(screen.getByRole('button', { name: 'Φόρτωση παλαιότερων' }));
    expect(onLoadOlder).toHaveBeenCalledOnce();
    rerender(
      <Timeline
        aria-label="Ιστορικό"
        events={events}
        now={now}
        hasMore
        onLoadOlder={onLoadOlder}
        isLoadingOlder
      />,
    );
    expect(screen.getByRole('region', { name: 'Ιστορικό' })).toHaveAttribute('aria-busy', 'true');
  });

  it('marks live inserts and offers no edit affordances', () => {
    renderWithDs(
      <Timeline
        aria-label="Ιστορικό"
        events={[{ ...events[3], id: 'n', isNew: true } as TimelineEvent]}
        now={now}
        showFilter={false}
      />,
    );
    expect(screen.getByRole('listitem')).toHaveAttribute('data-new', 'true');
    expect(screen.queryByRole('button', { name: /Επεξεργασία|Διαγραφή/ })).toBeNull();
  });

  it('renders loading and error states', () => {
    const { rerender, container } = renderWithDs(
      <Timeline aria-label="Ιστορικό" events={[]} state="loading" showFilter={false} />,
    );
    expect(container.querySelector('[aria-busy="true"]')).not.toBeNull();
    rerender(
      <Timeline
        aria-label="Ιστορικό"
        events={[]}
        state="error"
        onRetry={() => undefined}
        showFilter={false}
      />,
    );
    expect(within(container).getByRole('alert')).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Timeline aria-label="History" events={events} now={now} />);
    expect(screen.getByText('Today')).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Field changes' })).toBeInTheDocument();
    expect(screen.getByText('Ημερομηνία λήξης: from 31/12/2026 to 30/06/2027')).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <Timeline
        aria-label="Ιστορικό"
        events={events}
        now={now}
        hasMore
        onLoadOlder={() => undefined}
      />,
    );
    await expectNoA11yViolations(container);
  });
});
