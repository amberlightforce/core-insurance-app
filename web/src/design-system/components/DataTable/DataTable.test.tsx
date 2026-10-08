import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { Download } from 'lucide-react';
import { Profiler, useState } from 'react';
import { describe, expect, it, vi } from 'vitest';

// jsdom is slow with React Aria overlays and axe on a full grid.
vi.setConfig({ testTimeout: 20000 });

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { StatusPill } from '../StatusPill';
import {
  actionsColumn,
  booleanColumn,
  dateColumn,
  identifierColumn,
  moneyColumn,
  priorityColumn,
  queueColumn,
  statusColumn,
  textColumn,
} from './columns';
import { DataTable, type DataTableProps } from './DataTable';
import { estimateColumnWidth, includesNormalized, tableCollator } from './tableLogic';

interface Policy {
  id: string;
  holder: string;
  branch: string;
  status: 'inForce' | 'cancelled' | 'lapsed';
  premium: number;
  start: string;
  note: string;
  priority: number;
  paperless: boolean;
}

const policies: Policy[] = [
  {
    id: 'ΑΣΦ-2026-000010',
    holder: 'Παπαδόπουλος Γεώργιος',
    branch: 'Αυτοκίνητο',
    status: 'inForce',
    premium: 412.38,
    start: '2026-01-15',
    note: 'Ζημία 2025',
    priority: 82,
    paperless: true,
  },
  {
    id: 'ΑΣΦ-2026-000002',
    holder: 'Άλφα Ανώνυμη Εταιρεία',
    branch: 'Κατοικία',
    status: 'cancelled',
    premium: -128.4,
    start: '2026-03-01',
    note: '',
    priority: 40,
    paperless: false,
  },
  {
    id: 'ΑΣΦ-2026-000003',
    holder: 'Βασιλείου Μαρία',
    branch: 'Αυτοκίνητο',
    status: 'lapsed',
    premium: 980,
    start: '2025-11-20',
    note: 'Χωρίς ζημίες',
    priority: 65,
    paperless: true,
  },
  {
    id: 'ΑΣΦ-2026-000001',
    holder: 'αλέξης Κωνσταντίνου',
    branch: 'Κατοικία',
    status: 'inForce',
    premium: 230,
    start: '2026-02-10',
    note: 'Ανανέωση',
    priority: 12,
    paperless: false,
  },
];

const columns = [
  identifierColumn<Policy>('id', 'Ασφαλιστήριο', (r) => r.id, { size: 150 }),
  textColumn<Policy>('holder', 'Λήπτης', (r) => r.holder, { meta: { filter: 'text' } }),
  textColumn<Policy>('branch', 'Κλάδος', (r) => r.branch, {
    meta: {
      filter: 'enum',
      enumOptions: [
        { value: 'Αυτοκίνητο', label: 'Αυτοκίνητο' },
        { value: 'Κατοικία', label: 'Κατοικία' },
      ],
    },
  }),
  statusColumn<Policy>(
    'status',
    'Κατάσταση',
    (r) => r.status,
    (r) => <StatusPill entity="policyTerm" state={r.status} announceChanges={false} />,
  ),
  moneyColumn<Policy>('premium', 'Ασφάλιστρο', (r) => r.premium),
  dateColumn<Policy>('start', 'Έναρξη', (r) => r.start),
  textColumn<Policy>('note', 'Σημείωση', (r) => r.note),
  priorityColumn<Policy>('priority', 'Προτεραιότητα', (r) => r.priority),
  booleanColumn<Policy>('paperless', 'Ηλεκτρονικά', (r) => r.paperless),
];

type Props = Partial<DataTableProps<Policy>>;

function renderTable(props: Props = {}, preferences = {}) {
  return renderWithDs(
    <DataTable<Policy>
      aria-label="Ασφαλιστήρια"
      columns={columns}
      data={policies}
      getRowId={(r) => r.id}
      {...props}
    />,
    { preferences },
  );
}

/** The table (a treegrid while grouped). `hidden` reaches it while a modal popover is open. */
const grid = (hidden = false) =>
  screen.queryByRole('grid', { name: 'Ασφαλιστήρια', hidden }) ??
  screen.getByRole('treegrid', { name: 'Ασφαλιστήρια', hidden });
const bodyRows = (hidden = false) => within(grid(hidden)).getAllByRole('row', { hidden }).slice(1);
const holderOrder = () =>
  bodyRows().map((row) => within(row).getAllByRole('gridcell')[1]?.textContent ?? '');
function firstRow(): HTMLElement {
  const row = bodyRows()[0];
  if (!row) throw new Error('No body rows');
  return row;
}
const header = (name: string) => screen.getByRole('columnheader', { name: new RegExp(name) });

describe('table logic', () => {
  it('collates Greek with numeric ordering and accent-insensitive matching', () => {
    expect(tableCollator.compare('ΑΣΦ-2', 'ΑΣΦ-10')).toBeLessThan(0);
    expect(tableCollator.compare('άλφα', 'αλφα')).toBe(0);
    expect(includesNormalized('Ζημία 2025', 'ζημια')).toBe(true);
    expect(includesNormalized('Ανανέωση', 'ζημια')).toBe(false);
  });

  it('estimates an autosize width within 64–480 px', () => {
    expect(estimateColumnWidth('Α', ['β'])).toBe(64);
    expect(estimateColumnWidth('Λήπτης', ['x'.repeat(200)])).toBe(480);
  });
});

describe('DataTable', () => {
  it('renders grid semantics with row and column counts', () => {
    renderTable();
    const g = grid();
    expect(g).toHaveAttribute('aria-rowcount', '5');
    expect(g).toHaveAttribute('aria-colcount', String(columns.length));
    const rows = within(g).getAllByRole('row');
    expect(rows[0]).toHaveAttribute('aria-rowindex', '1');
    expect(rows[1]).toHaveAttribute('aria-rowindex', '2');
    expect(rows[4]).toHaveAttribute('aria-rowindex', '5');
    expect(within(g).getAllByRole('columnheader')).toHaveLength(columns.length);
    expect(screen.getByText('4 εγγραφές')).toBeInTheDocument();
  });

  it('formats cells: money with U+2212 and adverse, dates, empty values, priority, boolean', () => {
    renderTable();
    const adverse = screen.getByText('−128,40 €');
    expect(adverse).toHaveAttribute('data-adverse', 'true');
    expect(screen.getByText('15/01/2026')).toBeInTheDocument();
    expect(screen.getAllByText('κενό').length).toBeGreaterThan(0);
    expect(screen.getByText('Προτεραιότητα 82 από 100')).toBeInTheDocument();
    expect(screen.getAllByRole('img', { name: 'Ναι' })).toHaveLength(2);
    expect(header('Ασφάλιστρο')).toHaveAttribute('data-align', 'end');
  });

  it('sorts asc → desc → clear with aria-sort and Greek collation', async () => {
    const { user } = renderTable();
    const sort = within(header('Λήπτης')).getByRole('button', { name: 'Λήπτης' });
    await user.click(sort);
    expect(header('Λήπτης')).toHaveAttribute('aria-sort', 'ascending');
    expect(holderOrder()).toEqual([
      'αλέξης Κωνσταντίνου',
      'Άλφα Ανώνυμη Εταιρεία',
      'Βασιλείου Μαρία',
      'Παπαδόπουλος Γεώργιος',
    ]);
    await user.click(sort);
    expect(header('Λήπτης')).toHaveAttribute('aria-sort', 'descending');
    expect(holderOrder()[0]).toBe('Παπαδόπουλος Γεώργιος');
    await user.click(sort);
    expect(header('Λήπτης')).not.toHaveAttribute('aria-sort');
  });

  it('multi-sorts with Shift+click, numbering the sorts and keeping aria-sort on the primary', async () => {
    const { user } = renderTable();
    await user.click(within(header('Κλάδος')).getByRole('button', { name: 'Κλάδος' }));
    await user.keyboard('{Shift>}');
    await user.click(within(header('Ασφάλιστρο')).getByRole('button', { name: 'Ασφάλιστρο' }));
    await user.keyboard('{/Shift}');
    expect(header('Κλάδος')).toHaveAttribute('aria-sort', 'ascending');
    expect(header('Ασφάλιστρο')).not.toHaveAttribute('aria-sort');
    expect(header('Ασφάλιστρο')).toHaveTextContent('2');
    expect(
      within(header('Ασφάλιστρο')).getByText('προτεραιότητα ταξινόμησης 2'),
    ).toBeInTheDocument();
    expect(holderOrder()).toEqual([
      'Παπαδόπουλος Γεώργιος',
      'Βασιλείου Μαρία',
      'Άλφα Ανώνυμη Εταιρεία',
      'αλέξης Κωνσταντίνου',
    ]);
  });

  it('selects with the tri-state header checkbox and labelled row checkboxes', async () => {
    const onSelectionChange = vi.fn();
    const { user } = renderTable({ selectable: true, onSelectionChange });
    expect(grid()).toHaveAttribute('aria-multiselectable', 'true');
    const all = screen.getByRole('checkbox', { name: 'Επιλογή όλων' });
    await user.click(all);
    expect(all).toBeChecked();
    for (const row of bodyRows()) expect(row).toHaveAttribute('aria-selected', 'true');
    await user.click(screen.getByRole('checkbox', { name: 'Επιλογή ΑΣΦ-2026-000002' }));
    expect(all).not.toBeChecked();
    expect((all as HTMLInputElement).indeterminate).toBe(true);
    expect(onSelectionChange).toHaveBeenLastCalledWith([
      'ΑΣΦ-2026-000010',
      'ΑΣΦ-2026-000003',
      'ΑΣΦ-2026-000001',
    ]);
    expect(screen.getByText('· 3 επιλεγμένα')).toBeInTheDocument();
  });

  it('selects a range with Shift+click', async () => {
    const { user } = renderTable({ selectable: true, getRowLabel: (r) => r.id });
    await user.click(screen.getByRole('checkbox', { name: 'Επιλογή ΑΣΦ-2026-000010' }));
    await user.keyboard('{Shift>}');
    await user.click(screen.getByRole('checkbox', { name: 'Επιλογή ΑΣΦ-2026-000003' }));
    await user.keyboard('{/Shift}');
    const selected = bodyRows().map((row) => row.getAttribute('aria-selected'));
    expect(selected).toEqual(['true', 'true', 'true', 'false']);
  });

  it('moves the cursor with J/K and arrows, opens with Enter/O, quick-looks with Space, X selects', async () => {
    const onOpen = vi.fn();
    const onQuickLook = vi.fn();
    const onCursorChange = vi.fn();
    const { user } = renderTable({ selectable: true, onOpen, onQuickLook, onCursorChange });
    const first = bodyRows()[0];
    expect(first).toHaveAttribute('tabindex', '0');
    first?.focus();
    await user.keyboard('j');
    expect(bodyRows()[1]).toHaveFocus();
    expect(bodyRows()[1]).toHaveAttribute('data-cursor', 'true');
    expect(onCursorChange).toHaveBeenLastCalledWith(policies[1]);
    await user.keyboard('{ArrowDown}');
    expect(bodyRows()[2]).toHaveFocus();
    await user.keyboard('k');
    expect(bodyRows()[1]).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(onOpen).toHaveBeenLastCalledWith(policies[1]);
    await user.keyboard('o');
    expect(onOpen).toHaveBeenCalledTimes(2);
    await user.keyboard(' ');
    expect(onQuickLook).toHaveBeenCalledWith(policies[1]);
    await user.keyboard('x');
    expect(bodyRows()[1]).toHaveAttribute('aria-selected', 'true');
    await user.keyboard('{Shift>}J{/Shift}');
    expect(bodyRows()[2]).toHaveAttribute('aria-selected', 'true');
    expect(bodyRows()[2]).toHaveFocus();
    await user.keyboard('{End}');
    expect(bodyRows()[3]).toHaveFocus();
    await user.keyboard('{Home}');
    expect(bodyRows()[0]).toHaveFocus();
  });

  it('opens on double-click', async () => {
    const onOpen = vi.fn();
    const { user } = renderTable({ onOpen });
    await user.dblClick(screen.getByText('Βασιλείου Μαρία'));
    expect(onOpen).toHaveBeenCalledWith(policies[2]);
  });

  it('turns single-key shortcuts off with the preference, keeping arrows', async () => {
    const { user } = renderTable({}, { singleKeyShortcuts: false });
    bodyRows()[0]?.focus();
    await user.keyboard('j');
    expect(bodyRows()[0]).toHaveFocus();
    await user.keyboard('{ArrowDown}');
    expect(bodyRows()[1]).toHaveFocus();
  });

  it('Ctrl+A selects all loaded rows', async () => {
    const { user } = renderTable({ selectable: true });
    bodyRows()[0]?.focus();
    await user.keyboard('{Control>}a{/Control}');
    for (const row of bodyRows()) expect(row).toHaveAttribute('aria-selected', 'true');
  });

  it('searches accent-insensitively and focuses search with /', async () => {
    const { user } = renderTable();
    bodyRows()[0]?.focus();
    await user.keyboard('/');
    const search = screen.getByRole('searchbox', { name: 'Αναζήτηση στον πίνακα' });
    expect(search).toHaveFocus();
    await user.keyboard('ζημια');
    // «ζημια» finds «Ζημία 2025» (accents and case ignored) but not «ζημίες».
    await waitFor(() => {
      expect(holderOrder()).toEqual(['Παπαδόπουλος Γεώργιος']);
    });
    expect(screen.getByText('1 εγγραφή')).toBeInTheDocument();
  });

  it('shows the filtered-empty state and clears filters', async () => {
    const { user } = renderTable();
    await user.type(screen.getByRole('searchbox'), 'ανύπαρκτο');
    expect(await screen.findByText('Κανένα αποτέλεσμα με αυτά τα φίλτρα')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Εκκαθάριση φίλτρων' }));
    await waitFor(() => {
      expect(bodyRows()).toHaveLength(4);
    });
  });

  it('filters by an enum column from the header and shows a removable chip', async () => {
    const { user } = renderTable();
    await user.click(screen.getByRole('button', { name: 'Φίλτρο: Κλάδος' }));
    const dialog = screen.getByRole('dialog', { name: 'Φίλτρο: Κλάδος' });
    await user.click(within(dialog).getByRole('checkbox', { name: 'Κατοικία' }));
    expect(bodyRows(true)).toHaveLength(2);
    await user.keyboard('{Escape}');
    const chip = screen.getByRole('row', { name: 'Κλάδος: Κατοικία' });
    chip.focus();
    await user.keyboard('{Delete}');
    expect(bodyRows()).toHaveLength(4);
  });

  it('filters a text column with «Περιέχει»', async () => {
    const { user } = renderTable();
    await user.click(screen.getByRole('button', { name: 'Φίλτρο: Λήπτης' }));
    await user.type(screen.getByRole('textbox', { name: 'Περιέχει' }), 'μαρια');
    await user.keyboard('{Escape}');
    expect(holderOrder()).toEqual(['Βασιλείου Μαρία']);
    expect(screen.getByRole('row', { name: 'Λήπτης: μαρια' })).toBeInTheDocument();
  });

  it('hides, reorders and resets columns from the settings popover', async () => {
    const { user } = renderTable();
    await user.click(screen.getByRole('button', { name: 'Στήλες' }));
    const dialog = screen.getByRole('dialog', { name: 'Εμφάνιση στηλών' });
    const names = () =>
      screen.getAllByRole('columnheader', { hidden: true }).map((h) => h.textContent);
    await user.click(within(dialog).getByRole('checkbox', { name: 'Σημείωση' }));
    expect(names().some((n) => n.includes('Σημείωση'))).toBe(false);
    await user.click(within(dialog).getByRole('button', { name: 'Μετακίνηση πάνω: Κλάδος' }));
    expect(names()[1]).toContain('Κλάδος');
    expect(names()[2]).toContain('Λήπτης');
    expect(
      within(dialog).getByRole('button', { name: 'Μετακίνηση πάνω: Ασφαλιστήριο' }),
    ).toBeDisabled();
    await user.click(within(dialog).getByRole('button', { name: 'Επαναφορά προεπιλογών' }));
    expect(names().some((n) => n.includes('Σημείωση'))).toBe(true);
    expect(names()[1]).toContain('Λήπτης');
  });

  it('pins the selection and identifying columns left and actions right', () => {
    const withActions = [
      ...columns,
      actionsColumn<Policy>('Ενέργειες', (r) => (
        <button type="button" aria-label={`Ενέργειες ${r.id}`}>
          ⋯
        </button>
      )),
    ];
    renderTable({ selectable: true, columns: withActions, pinnedColumns: { left: ['id'] } });
    const headers = screen.getAllByRole('columnheader');
    expect(headers[0]).toHaveAttribute('data-pinned', 'left');
    expect(headers[1]).toHaveAttribute('data-pinned', 'left');
    expect(headers[1]).toHaveTextContent('Ασφαλιστήριο');
    expect(headers[2]).not.toHaveAttribute('data-pinned');
    expect(headers.at(-1)).toHaveAttribute('data-pinned', 'right');
    const cells = within(firstRow()).getAllByRole('gridcell');
    expect(cells[1]).toHaveAttribute('data-pinned', 'left');
    expect(cells.at(-1)).toHaveAttribute('data-pinned', 'right');
  });

  it('resizes a column with the keyboard', async () => {
    const { user } = renderTable();
    const handle = screen.getByRole('separator', { name: 'Αλλαγή πλάτους στήλης Λήπτης' });
    expect(handle).toHaveAttribute('aria-valuenow', '160');
    handle.focus();
    await user.keyboard('{ArrowRight}');
    expect(handle).toHaveAttribute('aria-valuenow', '176');
    within(header('Λήπτης')).getByRole('button', { name: 'Λήπτης' }).focus();
    await user.keyboard('{Control>}{Alt>}{ArrowLeft}{/Alt}{/Control}');
    expect(handle).toHaveAttribute('aria-valuenow', '160');
  });

  it('groups rows with expandable group rows and counts', async () => {
    const { user } = renderTable({ grouping: ['branch'] });
    const group = screen.getByRole('row', { name: 'Κλάδος: Αυτοκίνητο, 2 εγγραφές' });
    expect(group).toHaveAttribute('aria-expanded', 'true');
    expect(bodyRows()).toHaveLength(6);
    await user.click(group);
    expect(screen.getByRole('row', { name: 'Κλάδος: Αυτοκίνητο, 2 εγγραφές' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
    expect(bodyRows()).toHaveLength(4);
  });

  it('shows the empty, loading, refetch, error, stale, permission and hidden-normal states', async () => {
    const onRetry = vi.fn();
    const onRefresh = vi.fn();
    const onShowAll = vi.fn();
    const { rerender, user } = renderTable({ data: [] });
    expect(screen.getByText('Δεν υπάρχουν εγγραφές')).toBeInTheDocument();

    rerender(
      <DataTable<Policy>
        aria-label="Ασφαλιστήρια"
        columns={columns}
        data={[]}
        getRowId={(r) => r.id}
        isLoading
      />,
    );
    expect(grid()).toHaveAttribute('aria-busy', 'true');
    expect(grid()).toHaveAttribute('aria-rowcount', '-1');
    expect(bodyRows()).toHaveLength(8);
    expect(screen.getByRole('status', { name: '' })).toBeInTheDocument();

    rerender(
      <DataTable<Policy>
        aria-label="Ασφαλιστήρια"
        columns={columns}
        data={policies}
        getRowId={(r) => r.id}
        isRefetching
        error={{ onRetry }}
        staleCount={12}
        onRefresh={onRefresh}
        permissionLimited
        hiddenNormalCount={4721}
        onShowAll={onShowAll}
      />,
    );
    expect(screen.getByRole('progressbar', { name: 'Ανανέωση δεδομένων…' })).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('Δεν ήταν δυνατή η φόρτωση των εγγραφών.');
    await user.click(within(screen.getByRole('alert')).getByRole('button', { name: 'Επανάληψη' }));
    expect(onRetry).toHaveBeenCalled();
    expect(bodyRows().length).toBeGreaterThanOrEqual(4);
    await user.click(screen.getByRole('button', { name: 'Ανανέωση' }));
    expect(onRefresh).toHaveBeenCalled();
    expect(screen.getByText('12 εγγραφές άλλαξαν')).toBeInTheDocument();
    expect(grid()).toHaveAccessibleDescription(
      'Ορισμένες στήλες δεν εμφανίζονται λόγω δικαιωμάτων',
    );
    expect(screen.getByText('4.721 κανονικές εγγραφές κρυμμένες')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Εμφάνιση όλων' }));
    expect(onShowAll).toHaveBeenCalled();
  });

  it('renders row states: locked, error with retry, new', async () => {
    const onRetry = vi.fn();
    const { user } = renderTable({
      selectable: true,
      getRowState: (r) =>
        r.id === 'ΑΣΦ-2026-000002'
          ? { lockedReason: 'Κλειδωμένο από Μ. Παπαδοπούλου' }
          : r.id === 'ΑΣΦ-2026-000003'
            ? { error: { message: 'Η ενημέρωση απέτυχε.', onRetry } }
            : r.id === 'ΑΣΦ-2026-000001'
              ? { isNew: true }
              : undefined,
    });
    const locked = screen.getByRole('row', { name: /Άλφα Ανώνυμη Εταιρεία/ });
    expect(locked).toHaveAttribute('aria-disabled', 'true');
    expect(within(locked).getByRole('checkbox')).toBeDisabled();
    expect(within(locked).getByText('Κλειδωμένο από Μ. Παπαδοπούλου')).toBeInTheDocument();
    expect(screen.getByRole('row', { name: /Βασιλείου Μαρία/ })).toHaveAttribute(
      'data-error',
      'true',
    );
    const message = screen.getByRole('row', { name: /Η ενημέρωση απέτυχε/ });
    await user.click(within(message).getByRole('button', { name: 'Επανάληψη' }));
    expect(onRetry).toHaveBeenCalled();
    expect(screen.getByRole('row', { name: /αλέξης/ })).toHaveAttribute('data-new', 'true');
    expect(screen.getByText('Νέο')).toBeInTheDocument();
    expect(grid()).toHaveAttribute('aria-rowcount', '6');
  });

  it('virtualises large data with aria-rowcount and aria-rowindex', () => {
    const many = Array.from({ length: 1000 }, (_, i) => ({
      ...policies[0],
      id: `ΑΣΦ-2026-${String(i + 1).padStart(6, '0')}`,
      holder: `Πελάτης ${String(i + 1)}`,
    })) as Policy[];
    // jsdom has no layout: give the scroll viewport a size so the virtualiser renders a window.
    const height = vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(600);
    const width = vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(1200);
    renderTable({ data: many });
    height.mockRestore();
    width.mockRestore();
    expect(grid()).toHaveAttribute('aria-rowcount', '1001');
    const rows = bodyRows();
    expect(rows.length).toBeGreaterThan(0);
    expect(rows.length).toBeLessThan(100);
    expect(rows[0]).toHaveAttribute('aria-rowindex', '2');
    expect(rows[0]).toHaveStyle({ transform: 'translateY(0px)' });
  });

  it('pages with the Pagination component and absolute row indexes', async () => {
    const { user } = renderTable({ pagination: { pageSize: 25 } });
    const nav = screen.getByRole('navigation', { name: 'Σελιδοποίηση' });
    expect(nav).toHaveTextContent('1–4 από 4');
    expect(bodyRows()).toHaveLength(4);
    expect(grid()).toHaveAttribute('aria-rowcount', '5');
    await user.click(within(nav).getByRole('button', { name: /Εγγραφές ανά σελίδα/ }));
    expect(screen.getAllByRole('option')).toHaveLength(3);
  });

  it('shows the floating bulk bar and offers to select all matching', async () => {
    const onArchive = vi.fn();
    const onSelectAllMatching = vi.fn();
    const { user } = renderTable({
      selectable: true,
      totalCount: 4812,
      onSelectAllMatching,
      bulkActions: [{ id: 'export', label: 'Εξαγωγή', icon: Download, onAction: onArchive }],
    });
    await user.click(screen.getByRole('checkbox', { name: 'Επιλογή ΑΣΦ-2026-000010' }));
    const bar = await screen.findByRole('toolbar', { name: 'Μαζικές ενέργειες' });
    expect(within(bar).getByText('1 επιλεγμένο')).toBeInTheDocument();
    await user.click(within(bar).getByRole('button', { name: 'Εξαγωγή' }));
    expect(onArchive).toHaveBeenCalledWith(['ΑΣΦ-2026-000010']);

    await user.click(screen.getByRole('checkbox', { name: 'Επιλογή όλων' }));
    await user.click(screen.getByRole('button', { name: 'Επιλογή και των 4.812 που ταιριάζουν' }));
    expect(onSelectAllMatching).toHaveBeenCalled();

    await user.click(within(bar).getByRole('button', { name: 'Εκκαθάριση επιλογής' }));
    await waitFor(() => {
      expect(screen.queryByRole('toolbar', { name: 'Μαζικές ενέργειες' })).not.toBeInTheDocument();
    });
  });

  it('renders two-line queue rows and a labelled money total', () => {
    const queue = [
      queueColumn<Policy>('client', 'Πελάτης', (r) => ({
        primary: r.holder,
        id: r.id,
        fact: r.branch,
      })),
      moneyColumn<Policy>('premium', 'Ασφάλιστρο', (r) => r.premium),
    ];
    renderTable({
      columns: queue,
      rowVariant: 'queue',
      footerTotal: { label: 'Σύνολο ασφαλίστρων', value: 1494.0 },
    });
    expect(bodyRows()[0]).toHaveAttribute('data-variant', 'queue');
    expect(within(firstRow()).getByText('ΑΣΦ-2026-000010')).toBeInTheDocument();
    expect(screen.getByText('Σύνολο ασφαλίστρων: 1.494,00 €')).toBeInTheDocument();
  });

  it('switches the per-table density', async () => {
    const { user, container } = renderTable();
    await user.click(screen.getByRole('radio', { name: 'Άνετη' }));
    expect(container.querySelector('[data-density="comfortable"]')).not.toBeNull();
  });

  it('renders a card list below 600 px of container width', async () => {
    const original = globalThis.ResizeObserver;
    globalThis.ResizeObserver = class {
      constructor(private readonly callback: ResizeObserverCallback) {}
      observe() {
        this.callback([{ contentRect: { width: 420 } } as ResizeObserverEntry], this);
      }
      unobserve() {
        return undefined;
      }
      disconnect() {
        return undefined;
      }
    };
    const onOpen = vi.fn();
    try {
      const { user } = renderTable({ selectable: true, onOpen });
      expect(screen.queryByRole('grid')).not.toBeInTheDocument();
      const list = screen.getByRole('list', { name: 'Ασφαλιστήρια' });
      expect(within(list).getAllByRole('listitem')).toHaveLength(4);
      await user.click(screen.getByRole('checkbox', { name: 'Επιλογή ΑΣΦ-2026-000003' }));
      expect(screen.getByText('· 1 επιλεγμένο')).toBeInTheDocument();
      await user.click(within(list).getByRole('button', { name: 'ΑΣΦ-2026-000003' }));
      expect(onOpen).toHaveBeenCalledWith(policies[2]);
    } finally {
      globalThis.ResizeObserver = original;
    }
  });

  it.each([
    ['unpaged', undefined],
    ['paged', { pageSize: 10 }],
  ])(
    'does not loop when its parent hands it a new array on every render (%s)',
    (_name, pagination) => {
      // `query.data?.items ?? []` and `.map(...)` build a new array on every render. The table must treat the same
      // rows as unchanged instead of resetting its state, re-rendering and receiving yet another array, forever.
      let commits = 0;
      function Parent() {
        const [, setTick] = useState(0);
        return (
          <Profiler
            id="table"
            onRender={() => {
              commits += 1;
            }}
          >
            <button
              onClick={() => {
                setTick((n) => n + 1);
              }}
            >
              tick
            </button>
            <DataTable<Policy>
              aria-label="Ασφαλιστήρια"
              columns={[textColumn<Policy>('holder', 'Λήπτης', (p) => p.holder)]}
              data={[...policies]}
              getRowId={(p) => p.id}
              {...(pagination ? { pagination } : {})}
            />
          </Profiler>
        );
      }
      renderWithDs(<Parent />);
      expect(commits).toBeLessThan(15);
      fireEvent.click(screen.getByRole('button', { name: 'tick' }));
      expect(commits).toBeLessThan(25);
    },
  );

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderTable({ selectable: true, data: [] });
    expect(screen.getByText('No records')).toBeInTheDocument();
    expect(screen.getByRole('searchbox', { name: 'Search the table' })).toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Select all' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    renderTable({ selectable: true, grouping: ['branch'], pinnedColumns: { left: ['id'] } });
    await expectNoA11yViolations();
  });

  it('has no axe violations in the empty state', async () => {
    renderTable({ data: [] });
    const scroller = screen.getByRole('grid').parentElement;
    if (scroller) fireEvent.scroll(scroller);
    await expectNoA11yViolations();
  });
});
