import {
  flexRender,
  getCoreRowModel,
  getExpandedRowModel,
  getFilteredRowModel,
  getGroupedRowModel,
  getPaginationRowModel,
  getSortedRowModel,
  useReactTable,
  type Cell,
  type ColumnDef,
  type ColumnFiltersState,
  type ColumnOrderState,
  type ColumnSizingState,
  type ExpandedState,
  type FilterFn,
  type OnChangeFn,
  type PaginationState,
  type Row as TableRow,
  type RowSelectionState,
  type SortingFn,
  type SortingState,
  type VisibilityState,
} from '@tanstack/react-table';
import { useVirtualizer } from '@tanstack/react-virtual';
import { CircleAlert, RefreshCw, Search, WifiOff } from 'lucide-react';
import {
  useDeferredValue,
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
  type MouseEvent,
  type ReactNode,
} from 'react';
import {
  Input,
  SearchField,
  ToggleButton,
  ToggleButtonGroup,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { formatInteger, formatMoney } from '../../../format/numbers';
import { Icon } from '../../icons';
import { usePreferences } from '../../preferences/context';
import { tableHeaderHeight } from '../../tokens';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import { Pagination } from '../Pagination/Pagination';
import type { FilterChipItem } from '../Tags/chipText';
import { FilterChips } from '../Tags/Tags';
import { BulkActionBar } from './BulkActionBar';
import { ColumnSettings } from './ColumnSettings';
import styles from './DataTable.module.css';
import { DataTableHeader } from './DataTableHeader';
import { DataRow, SkeletonRow, SpanRow, type RowPosition } from './DataTableRow';
import { actionsColumnId, computeLayout, selectColumnId } from './layout';
import {
  buildItems,
  collateSort,
  displayText,
  enumFilter,
  globalSearchFilter,
  isTextEntry,
  rowHeightFor,
  textContainsFilter,
  type TableItem,
} from './tableLogic';
import type {
  DataTableBulkAction,
  DataTableDensity,
  DataTableError,
  DataTableFooterTotal,
  DataTableRowState,
  DataTableRowVariant,
} from './types';

export interface DataTableProps<Row> {
  'aria-label': string;
  columns: readonly ColumnDef<Row>[];
  data: readonly Row[];
  getRowId: (row: Row) => string;
  /** Business id for «Επιλογή {id}» (defaults to the row id). */
  getRowLabel?: (row: Row) => string;
  /** `single` (32/40/48) or the two-line `queue` row (60/64/72). */
  rowVariant?: DataTableRowVariant;
  /** Server-side total when only part of the data is loaded. */
  totalCount?: number;

  /** First load: header + 8 skeleton rows. */
  isLoading?: boolean;
  /** Refetch: 2 px bar under the header; rows stay. */
  isRefetching?: boolean;
  /** Error banner with retry; old rows stay. */
  error?: DataTableError;
  /** Offline: cached rows + banner (actions are the caller's to disable). */
  isOffline?: boolean;
  /** Replaces the default «Δεν υπάρχουν εγγραφές». */
  emptyState?: ReactNode;
  /** Columns were omitted for permission reasons. */
  permissionLimited?: boolean;
  /** «{count} εγγραφές άλλαξαν · Ανανέωση»; rows never reorder by themselves. */
  staleCount?: number;
  onRefresh?: () => void;
  /** Exception-first queues: «{count} κανονικές εγγραφές κρυμμένες · Εμφάνιση όλων». */
  hiddenNormalCount?: number;
  onShowAll?: () => void;

  selectable?: boolean;
  /** Controlled selection (row ids). */
  selectedIds?: readonly string[];
  onSelectionChange?: (ids: string[]) => void;
  /** Offered when every loaded row is selected and `totalCount` is larger. */
  onSelectAllMatching?: () => void;
  bulkActions?: readonly DataTableBulkAction[];

  /** Enter / O / double-click. */
  onOpen?: (row: Row) => void;
  /** Space. */
  onQuickLook?: (row: Row) => void;
  /** J/K and clicks move the cursor row (drives a detail pane from cache). */
  onCursorChange?: (row: Row) => void;
  getRowState?: (row: Row) => DataTableRowState | undefined;

  initialSorting?: SortingState;
  initialColumnVisibility?: VisibilityState;
  /** Group rows by these column ids (expandable group rows with counts). */
  grouping?: readonly string[];
  /** Pinned columns besides the selection (left) and actions (right) columns; at most 2 on the left. */
  pinnedColumns?: { left?: readonly string[]; right?: readonly string[] };

  /** Global accent-insensitive search in the toolbar (default on). */
  searchable?: boolean;
  /** Extra toolbar content at the start (saved-view switcher…). */
  toolbarStart?: ReactNode;
  /** Export button slot at the end of the toolbar. */
  exportSlot?: ReactNode;
  showDensityToggle?: boolean;
  showColumnSettings?: boolean;
  footerTotal?: DataTableFooterTotal;

  /** Force virtualisation (it switches on automatically above 200 rows). */
  virtualize?: boolean;
  /** Classic pagination instead of virtualisation (stable page references). */
  pagination?: { pageSize?: number; pageSizeOptions?: readonly number[] };
  /** Scroll viewport height (CSS length). */
  height?: string;
  /** Below this container width the table renders as a card list (default 600). */
  cardBreakpoint?: number;
}

const skeletonRows = 8;

function isPointerCoarse(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(pointer: coarse)').matches
    : false;
}

/**
 * Data table (Part 2 §4.35) on TanStack Table v8 + TanStack Virtual v3. `role="grid"` with
 * `aria-rowcount`/`aria-rowindex`, multi-sort with Greek collation, accent-insensitive search, column
 * filters, pinning, resizing, visibility and order, grouping, selection with a floating bulk bar,
 * J/K cursor and the keyboard model, row and table states, virtualisation or classic paging, and a
 * card list below 600 px.
 */
export function DataTable<Row>(props: DataTableProps<Row>) {
  const {
    columns,
    data,
    getRowId,
    getRowLabel,
    rowVariant = 'single',
    totalCount,
    isLoading = false,
    isRefetching = false,
    error,
    isOffline = false,
    emptyState,
    permissionLimited = false,
    staleCount,
    onRefresh,
    hiddenNormalCount,
    onShowAll,
    selectable = false,
    selectedIds,
    onSelectionChange,
    onSelectAllMatching,
    bulkActions,
    onOpen,
    onQuickLook,
    onCursorChange,
    getRowState,
    initialSorting,
    initialColumnVisibility,
    grouping,
    pinnedColumns,
    searchable = true,
    toolbarStart,
    exportSlot,
    showDensityToggle = true,
    showColumnSettings = true,
    footerTotal,
    virtualize,
    pagination,
    height,
    cardBreakpoint = 600,
  } = props;
  const { t } = useTranslation('ds');
  const { preferences } = usePreferences();
  const region = preferences.regionFormat;
  const sortHintId = useId();
  const captionId = useId();

  const [sorting, setSorting] = useState<SortingState>(initialSorting ?? []);
  const [columnFilters, setColumnFilters] = useState<ColumnFiltersState>([]);
  const [search, setSearch] = useState('');
  const deferredSearch = useDeferredValue(search);
  const [internalSelection, setInternalSelection] = useState<RowSelectionState>({});
  const [columnVisibility, setColumnVisibility] = useState<VisibilityState>(
    initialColumnVisibility ?? {},
  );
  const [columnOrder, setColumnOrder] = useState<ColumnOrderState>([]);
  const [columnSizing, setColumnSizing] = useState<ColumnSizingState>({});
  const [expanded, setExpanded] = useState<ExpandedState>(true);
  const [paging, setPaging] = useState<PaginationState>({
    pageIndex: 0,
    pageSize: pagination?.pageSize ?? 50,
  });
  const [densityOverride, setDensityOverride] = useState<DataTableDensity | null>(null);
  const [cursorKey, setCursorKey] = useState<string | null>(null);
  const [openFilter, setOpenFilter] = useState<string | null>(null);
  const [narrow, setNarrow] = useState(false);

  const rootRef = useRef<HTMLDivElement>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const anchorKey = useRef<string | null>(null);
  const pendingFocus = useRef<string | null>(null);

  const rowSelection = useMemo<RowSelectionState>(
    () =>
      selectedIds ? Object.fromEntries(selectedIds.map((id) => [id, true])) : internalSelection,
    [selectedIds, internalSelection],
  );
  const onRowSelectionChange: OnChangeFn<RowSelectionState> = (updater) => {
    const next = typeof updater === 'function' ? updater(rowSelection) : updater;
    if (!selectedIds) setInternalSelection(next);
    onSelectionChange?.(Object.keys(next).filter((id) => next[id]));
  };

  const allColumns = useMemo<ColumnDef<Row>[]>(() => {
    const select: ColumnDef<Row> = {
      id: selectColumnId,
      header: '',
      cell: () => null,
      size: 44,
      enableSorting: false,
      enableHiding: false,
      enableResizing: false,
      enableGlobalFilter: false,
      enableGrouping: false,
    };
    // Enum filters use the checklist function.
    const withFilters = columns.map((column) =>
      column.meta?.filter === 'enum' && column.filterFn === undefined
        ? { ...column, filterFn: enumFilter as FilterFn<Row> }
        : column,
    );
    return selectable ? [select, ...withFilters] : withFilters;
  }, [selectable, columns]);

  const hasActions = columns.some((column) => column.id === actionsColumnId);
  const columnPinning = {
    left: [...(selectable ? [selectColumnId] : []), ...(pinnedColumns?.left ?? []).slice(0, 2)],
    right: [...(pinnedColumns?.right ?? []), ...(hasActions ? [actionsColumnId] : [])],
  };
  const paged = pagination !== undefined;
  // Expandable group rows make the table a treegrid (rows carry aria-level / aria-expanded).
  const grouped = (grouping?.length ?? 0) > 0;

  // TanStack's table instance is mutable by design; the hook is the documented integration.
  // eslint-disable-next-line react-hooks/incompatible-library
  const table = useReactTable<Row>({
    data: data as Row[],
    columns: allColumns,
    getRowId: (row) => getRowId(row),
    state: {
      sorting,
      columnFilters,
      globalFilter: deferredSearch,
      rowSelection,
      columnVisibility,
      columnOrder,
      columnSizing,
      columnPinning,
      grouping: grouping ? [...grouping] : [],
      expanded,
      ...(paged ? { pagination: paging } : {}),
    },
    defaultColumn: {
      minSize: 64,
      maxSize: 640,
      size: 160,
      sortingFn: collateSort as SortingFn<Row>,
      filterFn: textContainsFilter as FilterFn<Row>,
    },
    columnResizeMode: 'onChange',
    enableMultiSort: true,
    maxMultiSortColCount: 3,
    isMultiSortEvent: (event) => (event as globalThis.MouseEvent).shiftKey,
    sortDescFirst: false,
    globalFilterFn: globalSearchFilter as FilterFn<Row>,
    getColumnCanGlobalFilter: (column) =>
      column.columnDef.enableGlobalFilter !== false && column.id !== selectColumnId,
    enableRowSelection: (row) => !row.getIsGrouped() && !getRowState?.(row.original)?.lockedReason,
    groupedColumnMode: false,
    autoResetExpanded: false,
    autoResetPageIndex: paged,
    onSortingChange: setSorting,
    onColumnFiltersChange: setColumnFilters,
    onRowSelectionChange,
    onColumnVisibilityChange: setColumnVisibility,
    onColumnOrderChange: setColumnOrder,
    onColumnSizingChange: setColumnSizing,
    onExpandedChange: setExpanded,
    onPaginationChange: setPaging,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: getSortedRowModel(),
    getFilteredRowModel: getFilteredRowModel(),
    getGroupedRowModel: getGroupedRowModel(),
    getExpandedRowModel: getExpandedRowModel(),
    ...(paged ? { getPaginationRowModel: getPaginationRowModel() } : {}),
  });

  const rows = table.getRowModel().rows;
  const items = useMemo(
    () => buildItems(rows, getRowState, hiddenNormalCount),
    [rows, getRowState, hiddenNormalCount],
  );
  const filteredLeafCount = table
    .getFilteredRowModel()
    .flatRows.filter((r) => !r.getIsGrouped()).length;
  const layout = computeLayout(table);

  // Density: per-table override of the surface density; touch raises the row height.
  const density = densityOverride ?? preferences.density;
  const touch = isPointerCoarse();
  const rowHeight = rowHeightFor(rowVariant, density, touch);
  const singleHeight = rowHeightFor('single', density, touch);

  const loadingFirst = isLoading && data.length === 0;
  const filtersActive = columnFilters.length > 0 || deferredSearch.trim() !== '';
  const filteredEmpty = !loadingFirst && data.length > 0 && rows.length === 0 && filtersActive;
  const empty = !loadingFirst && data.length === 0 && !error;

  const virtualOn = !paged && !narrow && (virtualize ?? items.length > 200);
  const virtualizer = useVirtualizer({
    count: virtualOn ? items.length : 0,
    getScrollElement: () => scrollRef.current,
    estimateSize: (index) => (items[index]?.kind === 'row' ? rowHeight : singleHeight),
    overscan: 8,
    scrollMargin: tableHeaderHeight,
  });
  useEffect(() => {
    virtualizer.measure();
  }, [virtualizer, rowHeight, singleHeight]);

  // Card list below the breakpoint (container width, not the viewport).
  useLayoutEffect(() => {
    const root = rootRef.current;
    if (!root || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver((entries) => {
      const width = entries[0]?.contentRect.width ?? 0;
      if (width > 0) setNarrow(width < cardBreakpoint);
    });
    observer.observe(root);
    return () => {
      observer.disconnect();
    };
  }, [cardBreakpoint]);

  const cursorIndex = cursorKey === null ? -1 : items.findIndex((item) => item.key === cursorKey);
  const tabbableIndex = cursorIndex >= 0 ? cursorIndex : 0;

  // Move focus to the cursor row after a keyboard move (0 ms, no animation); virtualised rows may need
  // one more render after scrolling into the window.
  useEffect(() => {
    const pending = pendingFocus.current;
    const item = items[cursorIndex];
    if (pending === null || item?.key !== pending) return;
    if (virtualOn) virtualizer.scrollToIndex(cursorIndex, { align: 'auto' });
    const el = rootRef.current?.querySelector<HTMLElement>(
      `[data-item-key="${CSS.escape(item.key)}"]`,
    );
    if (el) {
      pendingFocus.current = null;
      el.focus({ preventScroll: virtualOn });
      if (!virtualOn) el.scrollIntoView({ block: 'nearest' });
    }
  });

  const selectedCount = Object.values(rowSelection).filter(Boolean).length;
  const selectedList = Object.keys(rowSelection).filter((id) => rowSelection[id]);
  const allSelected = selectable && filteredLeafCount > 0 && table.getIsAllRowsSelected();
  const someSelected = selectable && selectedCount > 0;

  const setCursor = (index: number, focus: boolean) => {
    const item = items[index];
    if (!item) return;
    setCursorKey(item.key);
    if (focus) pendingFocus.current = item.key;
    if (item.kind === 'row') onCursorChange?.(item.row.original);
  };

  const selectRange = (from: number, to: number) => {
    const [a, b] = from <= to ? [from, to] : [to, from];
    const next: RowSelectionState = { ...rowSelection };
    for (let i = a; i <= b; i++) {
      const item = items[i];
      if (item?.kind === 'row' && item.row.getCanSelect()) next[item.row.id] = true;
    }
    onRowSelectionChange(next);
  };

  const toggleRow = (row: TableRow<Row>) => {
    if (!row.getCanSelect()) return;
    row.toggleSelected();
  };

  const move = (target: number, extend: boolean, from = Math.max(0, cursorIndex)) => {
    if (items.length === 0) return;
    const next = Math.min(items.length - 1, Math.max(0, target));
    if (extend && selectable) selectRange(from, next);
    setCursor(next, true);
  };

  const pageRows = () => {
    const viewport = scrollRef.current?.clientHeight ?? 0;
    return Math.max(1, Math.floor((viewport - tableHeaderHeight) / rowHeight) || 10);
  };

  const onGridKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const target = event.target as HTMLElement;
    if (!event.currentTarget.contains(target) || isTextEntry(target)) return;
    if (!target.closest('[data-body]')) return;
    const rowEl = target.closest<HTMLElement>('[data-item-key]');
    const onRowItself = rowEl === target;
    const single =
      preferences.singleKeyShortcuts && !event.ctrlKey && !event.metaKey && !event.altKey;
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key;
    // The focused row is the cursor even before the first keyboard move.
    const focusedIndex = itemIndexOf(rowEl);
    const current = cursorIndex >= 0 ? cursorIndex : Math.max(0, focusedIndex);
    const item = items[current];

    const handled = (() => {
      if (key === 'ArrowDown' || (single && key === 'j')) {
        move(cursorIndex < 0 && focusedIndex < 0 ? 0 : current + 1, event.shiftKey, current);
        return true;
      }
      if (key === 'ArrowUp' || (single && key === 'k')) {
        move(current - 1, event.shiftKey, current);
        return true;
      }
      if (key === 'Home') {
        move(0, false);
        return true;
      }
      if (key === 'End') {
        move(items.length - 1, false);
        return true;
      }
      if (key === 'PageDown') {
        move(current + pageRows(), false);
        return true;
      }
      if (key === 'PageUp') {
        move(current - pageRows(), false);
        return true;
      }
      if (!onRowItself) return false;
      if (
        item?.kind === 'group' &&
        (key === 'Enter' || key === 'ArrowRight' || key === 'ArrowLeft')
      ) {
        item.row.toggleExpanded(key === 'Enter' ? undefined : key === 'ArrowRight');
        return true;
      }
      if (item?.kind !== 'row') return false;
      if (key === 'Enter' || (single && key === 'o')) {
        onOpen?.(item.row.original);
        return true;
      }
      if (key === ' ') {
        onQuickLook?.(item.row.original);
        return true;
      }
      if (single && key === 'x' && selectable) {
        anchorKey.current = item.key;
        toggleRow(item.row);
        return true;
      }
      return false;
    })();
    if (handled) {
      event.preventDefault();
      event.stopPropagation();
    }
  };

  const onRootKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    const target = event.target as Node;
    if (!rootRef.current?.contains(target) || isTextEntry(event.target)) return;
    if (
      (event.ctrlKey || event.metaKey) &&
      !event.altKey &&
      event.key.toLowerCase() === 'a' &&
      selectable
    ) {
      event.preventDefault();
      table.toggleAllRowsSelected(true);
      return;
    }
    if (
      event.key === '/' &&
      preferences.singleKeyShortcuts &&
      searchable &&
      !event.ctrlKey &&
      !event.metaKey
    ) {
      event.preventDefault();
      searchRef.current?.focus();
    }
  };

  const itemIndexOf = (el: HTMLElement | null) => {
    const key = el?.dataset.itemKey;
    return key === undefined ? -1 : items.findIndex((item) => item.key === key);
  };

  const onBodyClick = (event: MouseEvent<HTMLDivElement>) => {
    const target = event.target as HTMLElement;
    const rowEl = target.closest<HTMLElement>('[data-item-key]');
    const index = itemIndexOf(rowEl);
    const item = items[index];
    if (!item) return;
    if (target.closest('[data-select]') && item.kind === 'row') {
      const anchor =
        anchorKey.current === null ? -1 : items.findIndex((i) => i.key === anchorKey.current);
      if (event.shiftKey && anchor >= 0) selectRange(anchor, index);
      else toggleRow(item.row);
      anchorKey.current = item.key;
      setCursor(index, false);
      return;
    }
    setCursor(index, false);
    if (target.closest('button, a, input')) return;
    if (item.kind === 'group') item.row.toggleExpanded();
  };

  const onBodyDoubleClick = (event: MouseEvent<HTMLDivElement>) => {
    const target = event.target as HTMLElement;
    if (target.closest('button, a, input')) return;
    const item = items[itemIndexOf(target.closest<HTMLElement>('[data-item-key]'))];
    if (item?.kind === 'row') onOpen?.(item.row.original);
  };

  const onScroll = () => {
    const el = scrollRef.current;
    const root = rootRef.current;
    if (!el || !root) return;
    // MI-22: pinned shadows only while content is scrolled under them.
    root.toggleAttribute('data-scrolled-start', el.scrollLeft > 0);
    root.toggleAttribute('data-scrolled-end', el.scrollLeft + el.clientWidth < el.scrollWidth - 1);
  };

  // Labels and counts.
  const pageOffset = paged ? paging.pageIndex * paging.pageSize : 0;
  const totalRows = paged
    ? filteredLeafCount
    : totalCount !== undefined && totalCount > data.length && !filtersActive
      ? totalCount + Math.max(0, items.length - rows.length)
      : items.length;
  const ariaRowCount = loadingFirst ? -1 : totalRows + 1;
  const countValue = filtersActive ? filteredLeafCount : (totalCount ?? filteredLeafCount);
  const colCount = layout.columns.length;

  const activeChips: FilterChipItem[] = columnFilters.map((filter) => {
    const column = table.getColumn(filter.id);
    const meta = column?.columnDef.meta;
    const raw = filter.value;
    const values = Array.isArray(raw)
      ? (raw as string[]).map((v) => meta?.enumOptions?.find((o) => o.value === v)?.label ?? v)
      : [displayText(raw)];
    return { id: filter.id, field: meta?.label ?? filter.id, values };
  });

  const clearFilters = () => {
    setColumnFilters([]);
    setSearch('');
  };

  const position = (index: number, start: number | null): RowPosition => {
    const item = items[index];
    const depth = item && item.kind !== 'hiddenNormal' ? item.row.depth : 0;
    return {
      ariaRowIndex: pageOffset + index + 2,
      itemKey: item?.key ?? String(index),
      tabbable: index === tabbableIndex,
      isCursor: index === cursorIndex,
      start,
      ...(grouped ? { level: item?.kind === 'message' ? depth + 2 : depth + 1 } : {}),
    };
  };

  const renderItem = (item: TableItem<Row>, index: number, start: number | null): ReactNode => {
    const pos = position(index, start);
    switch (item.kind) {
      case 'row': {
        const state = getRowState?.(item.row.original);
        return (
          <DataRow
            key={item.key}
            row={item.row}
            layout={layout.columns}
            layoutKey={layout.key}
            position={pos}
            selectable={selectable}
            isSelected={rowSelection[item.row.id] === true}
            selectLabel={t('dataTable.selectRow', {
              id: getRowLabel ? getRowLabel(item.row.original) : item.row.id,
            })}
            state={state}
            newLabel={t('dataTable.isNew')}
            variant={rowVariant}
          />
        );
      }
      case 'group': {
        const groupColumn = item.row.groupingColumnId
          ? table.getColumn(item.row.groupingColumnId)
          : undefined;
        const label = groupColumn?.columnDef.meta?.label ?? item.row.groupingColumnId ?? '';
        return (
          <SpanRow key={item.key} position={pos} kind="group" isExpanded={item.row.getIsExpanded()}>
            <span className={styles.groupLabel}>
              {t('dataTable.group', {
                value: `${label}: ${displayText(item.row.groupingValue)}`,
                count: item.row.subRows.length,
              })}
            </span>
          </SpanRow>
        );
      }
      case 'message':
        return (
          <SpanRow key={item.key} position={pos} kind="message">
            <span className={styles.messageText}>{item.message}</span>
            {item.onRetry ? (
              <Button variant="link" size="sm" onPress={item.onRetry}>
                {t('dataTable.retry')}
              </Button>
            ) : null}
          </SpanRow>
        );
      case 'hiddenNormal':
        return (
          <SpanRow key={item.key} position={pos} kind="hiddenNormal">
            <span>
              {t('dataTable.hiddenNormal', {
                count: hiddenNormalCount ?? 0,
                formatted: formatInteger(hiddenNormalCount ?? 0, region),
              })}
            </span>
            {onShowAll ? (
              <Button variant="link" size="sm" onPress={onShowAll}>
                {t('dataTable.showAll')}
              </Button>
            ) : null}
          </SpanRow>
        );
    }
  };

  const bodyStyle: CSSProperties | undefined = virtualOn
    ? { blockSize: virtualizer.getTotalSize(), position: 'relative' }
    : undefined;

  const emptyRow = (content: ReactNode) => (
    <SpanRow
      position={{
        ariaRowIndex: 2,
        itemKey: '__empty',
        tabbable: false,
        isCursor: false,
        start: null,
      }}
      kind="empty"
    >
      {content}
    </SpanRow>
  );

  const body = loadingFirst
    ? Array.from({ length: skeletonRows }, (_, i) => (
        <SkeletonRow key={i} layout={layout.columns} ariaRowIndex={i + 2} />
      ))
    : filteredEmpty
      ? emptyRow(
          <div className={styles.emptyBlock}>
            <p className={styles.emptyTitle}>{t('dataTable.filteredEmpty')}</p>
            {activeChips.length > 0 ? (
              <FilterChips items={activeChips} aria-label={t('tags.groupLabel')} />
            ) : null}
            <Button variant="secondary" size="sm" onPress={clearFilters}>
              {t('dataTable.clearFilters')}
            </Button>
          </div>,
        )
      : empty
        ? emptyRow(
            emptyState ?? (
              <div className={styles.emptyBlock}>
                <p className={styles.emptyTitle}>{t('dataTable.empty')}</p>
              </div>
            ),
          )
        : virtualOn
          ? virtualizer.getVirtualItems().map((v) => {
              const item = items[v.index];
              return item ? renderItem(item, v.index, v.start - tableHeaderHeight) : null;
            })
          : items.map((item, index) => renderItem(item, index, null));

  const rootStyle = {
    '--_cols': layout.template,
    '--_row-width': `${String(layout.width)}px`,
    ...(height ? { '--_height': height } : {}),
  } as CSSProperties;

  const describedBy = [permissionLimited ? captionId : null].filter(Boolean).join(' ');

  return (
    <div
      ref={rootRef}
      className={styles.root}
      style={rootStyle}
      data-density={densityOverride ?? undefined}
      data-row-variant={rowVariant}
      onKeyDown={onRootKeyDown}
    >
      <div className={styles.toolbar}>
        {toolbarStart}
        {searchable ? (
          <SearchField
            aria-label={t('dataTable.search')}
            className={cx(styles.search)}
            value={search}
            onChange={setSearch}
          >
            <Icon icon={Search} size={14} />
            <Input
              ref={searchRef}
              className={cx(styles.input)}
              placeholder={t('dataTable.search')}
            />
          </SearchField>
        ) : null}
        {activeChips.length > 0 ? (
          <FilterChips
            items={activeChips}
            onRemove={(id) => {
              table.getColumn(id)?.setFilterValue(undefined);
            }}
            onEdit={(id) => {
              setOpenFilter(id);
            }}
          />
        ) : null}
        <span className={styles.toolbarSpacer} />
        {showDensityToggle ? (
          <ToggleButtonGroup
            aria-label={t('dataTable.density')}
            className={cx(styles.segmented)}
            selectionMode="single"
            disallowEmptySelection
            selectedKeys={[density]}
            onSelectionChange={(keys: Set<Key>) => {
              const [first] = [...keys];
              if (first === 'compact' || first === 'comfortable') setDensityOverride(first);
            }}
          >
            <ToggleButton id="compact" className={cx(styles.segment)}>
              {t('dataTable.densityCompact')}
            </ToggleButton>
            <ToggleButton id="comfortable" className={cx(styles.segment)}>
              {t('dataTable.densityComfortable')}
            </ToggleButton>
          </ToggleButtonGroup>
        ) : null}
        {showColumnSettings ? <ColumnSettings table={table} /> : null}
        {exportSlot}
      </div>

      {permissionLimited ? (
        <p id={captionId} className={styles.caption}>
          {t('dataTable.permissionLimited')}
        </p>
      ) : null}
      {isOffline ? (
        <div role="status" className={styles.banner} data-tone="neutral">
          <Icon icon={WifiOff} size={16} />
          <span>{t('statusPill.semantic.offline')}</span>
        </div>
      ) : null}
      {error ? (
        <div role="alert" className={styles.banner} data-tone="danger">
          <Icon icon={CircleAlert} size={16} />
          <span>{error.message ?? t('dataTable.error')}</span>
          {error.onRetry ? (
            <Button variant="secondary" size="sm" onPress={error.onRetry}>
              {t('dataTable.retry')}
            </Button>
          ) : null}
        </div>
      ) : null}
      {staleCount !== undefined && staleCount > 0 ? (
        <div role="status" className={styles.banner} data-tone="warning">
          <Icon icon={RefreshCw} size={16} />
          <span>
            {t('dataTable.stale', {
              count: staleCount,
              formatted: formatInteger(staleCount, region),
            })}
          </span>
          {onRefresh ? (
            <Button variant="secondary" size="sm" onPress={onRefresh}>
              {t('dataTable.refresh')}
            </Button>
          ) : null}
        </div>
      ) : null}
      {allSelected &&
      onSelectAllMatching &&
      totalCount !== undefined &&
      totalCount > selectedCount ? (
        <div className={styles.selectAllStrip} role="status">
          <span>
            {t('dataTable.pageSelected', {
              count: selectedCount,
              formatted: formatInteger(selectedCount, region),
            })}
          </span>
          <Button variant="link" size="sm" onPress={onSelectAllMatching}>
            {t('dataTable.selectAllMatching', { formatted: formatInteger(totalCount, region) })}
          </Button>
        </div>
      ) : null}

      {narrow ? (
        <CardList
          rows={rows.filter((row) => !row.getIsGrouped())}
          selectable={selectable}
          rowSelection={rowSelection}
          getRowLabel={getRowLabel}
          onOpen={onOpen}
          label={props['aria-label']}
        />
      ) : (
        <div className={styles.viewport}>
          <div ref={scrollRef} className={styles.scroller} onScroll={onScroll}>
            <div
              role={grouped ? 'treegrid' : 'grid'}
              aria-label={props['aria-label']}
              aria-rowcount={ariaRowCount}
              aria-colcount={colCount}
              {...(selectable ? { 'aria-multiselectable': true } : {})}
              {...(loadingFirst || isRefetching ? { 'aria-busy': true } : {})}
              {...(describedBy ? { 'aria-describedby': describedBy } : {})}
              className={styles.grid}
              onKeyDown={onGridKeyDown}
            >
              <DataTableHeader
                table={table}
                layout={layout}
                selectable={selectable}
                allSelected={allSelected}
                someSelected={someSelected}
                onToggleAll={() => {
                  if (paged) table.toggleAllPageRowsSelected(!allSelected);
                  else table.toggleAllRowsSelected(!allSelected);
                }}
                openFilter={openFilter}
                onOpenFilterChange={setOpenFilter}
                sortHintId={sortHintId}
              />
              <div
                role="rowgroup"
                className={styles.body}
                style={bodyStyle}
                data-body
                data-virtual={virtualOn || undefined}
                onClick={onBodyClick}
                onDoubleClick={onBodyDoubleClick}
              >
                {body}
              </div>
            </div>
          </div>
          {isRefetching ? (
            <div
              className={styles.refetchBar}
              role="progressbar"
              aria-label={t('dataTable.refreshing')}
            />
          ) : null}
        </div>
      )}
      <span id={sortHintId} hidden>
        {t('dataTable.sortHint')}
      </span>
      {loadingFirst ? (
        <span className="ds-visually-hidden" role="status">
          {t('dataTable.loading')}
        </span>
      ) : null}

      <div className={styles.footer}>
        {paged ? (
          <Pagination
            page={paging.pageIndex + 1}
            pageSize={paging.pageSize}
            total={filteredLeafCount}
            onPageChange={(page) => {
              table.setPageIndex(page - 1);
            }}
            onPageSizeChange={(size) => {
              table.setPageSize(size);
            }}
            {...(pagination.pageSizeOptions ? { pageSizeOptions: pagination.pageSizeOptions } : {})}
          />
        ) : (
          <span className={styles.footerCount}>
            {t('dataTable.footerCount', {
              count: countValue,
              formatted: formatInteger(countValue, region),
            })}
          </span>
        )}
        {selectedCount > 0 ? (
          <span className={styles.footerCount}>
            ·{' '}
            {t('dataTable.footerSelected', {
              count: selectedCount,
              formatted: formatInteger(selectedCount, region),
            })}
          </span>
        ) : null}
        {footerTotal ? (
          <span className={styles.footerTotal}>
            {t('dataTable.total', {
              label: footerTotal.label,
              value: formatMoney(footerTotal.value, {
                region,
                ...(footerTotal.currency ? { currency: footerTotal.currency } : {}),
              }),
            })}
          </span>
        ) : null}
      </div>

      {selectable && bulkActions && bulkActions.length > 0 ? (
        <BulkActionBar
          count={selectedCount}
          selectedIds={selectedList}
          actions={bulkActions}
          onClear={() => {
            onRowSelectionChange({});
          }}
        />
      ) : null}
    </div>
  );
}

interface CardListProps<TData> {
  rows: readonly TableRow<TData>[];
  selectable: boolean;
  rowSelection: RowSelectionState;
  getRowLabel: ((row: TData) => string) | undefined;
  onOpen: ((row: TData) => void) | undefined;
  label: string;
}

/** Below 600 px (container): one card per record with the visible columns as a key–value list. */
function CardList<TData>({
  rows,
  selectable,
  rowSelection,
  getRowLabel,
  onOpen,
  label,
}: CardListProps<TData>) {
  const { t } = useTranslation('ds');
  return (
    <ul className={styles.cards} aria-label={label}>
      {rows.map((row) => {
        const cells = row.getVisibleCells().filter((cell) => cell.column.id !== selectColumnId);
        const [first, ...rest] = cells;
        const rowLabel = getRowLabel ? getRowLabel(row.original) : row.id;
        return (
          <li
            key={row.id}
            className={styles.card}
            data-selected={rowSelection[row.id] === true ? true : undefined}
          >
            <div className={styles.cardHead}>
              {selectable ? (
                <input
                  type="checkbox"
                  className={styles.rowCheckbox}
                  checked={rowSelection[row.id] === true}
                  disabled={!row.getCanSelect()}
                  onChange={() => {
                    row.toggleSelected();
                  }}
                  aria-label={t('dataTable.selectRow', { id: rowLabel })}
                />
              ) : null}
              {first ? (
                onOpen ? (
                  <button
                    type="button"
                    className={styles.cardTitle}
                    onClick={() => {
                      onOpen(row.original);
                    }}
                  >
                    {renderCell(first)}
                  </button>
                ) : (
                  <span className={styles.cardTitle}>{renderCell(first)}</span>
                )
              ) : null}
            </div>
            <dl className={styles.cardFacts}>
              {rest.map((cell) => (
                <div key={cell.id} className={styles.cardFact}>
                  <dt>{cell.column.columnDef.meta?.label ?? cell.column.id}</dt>
                  <dd>{renderCell(cell)}</dd>
                </div>
              ))}
            </dl>
          </li>
        );
      })}
    </ul>
  );
}

function renderCell<TData>(cell: Cell<TData, unknown>): ReactNode {
  return flexRender(cell.column.columnDef.cell, cell.getContext());
}
