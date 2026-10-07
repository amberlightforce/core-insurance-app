import { flexRender, type Column, type Table } from '@tanstack/react-table';
import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react';
import type { KeyboardEvent } from 'react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { ColumnFilter } from './ColumnFilter';
import styles from './DataTable.module.css';
import { selectColumnId, type TableLayout } from './layout';
import { autosizeColumn, columnSizeLimits, resizeColumnBy } from './tableLogic';

export interface DataTableHeaderProps<TData> {
  table: Table<TData>;
  layout: TableLayout<TData>;
  selectable: boolean;
  /** Tri-state header checkbox. */
  allSelected: boolean;
  someSelected: boolean;
  onToggleAll: () => void;
  openFilter: string | null;
  onOpenFilterChange: (id: string | null) => void;
  sortHintId: string;
}

/** Ctrl+Alt+←/→ resizes by 16 px from the header button or the handle (Part 2 §4.35). */
function resizeKeys<TData>(
  event: KeyboardEvent<HTMLElement>,
  table: Table<TData>,
  column: Column<TData>,
  plainArrows: boolean,
) {
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
  if (!plainArrows && !(event.ctrlKey && event.altKey)) return;
  event.preventDefault();
  event.stopPropagation();
  resizeColumnBy(
    table,
    column,
    event.key === 'ArrowRight' ? columnSizeLimits.step : -columnSizeLimits.step,
  );
}

/**
 * Sticky 38 px header row: sortable buttons (click asc → desc → clear; Shift+click adds up to 3 sorts,
 * shown ▲1 ▼2), `aria-sort` on the primary sort only, filter popovers, resize handles and the
 * tri-state selection checkbox.
 */
export function DataTableHeader<TData>({
  table,
  layout,
  selectable,
  allSelected,
  someSelected,
  onToggleAll,
  openFilter,
  onOpenFilterChange,
  sortHintId,
}: DataTableHeaderProps<TData>) {
  const { t } = useTranslation('ds');
  const sorting = table.getState().sorting;
  const multi = sorting.length > 1;
  const headers = table.getHeaderGroups().at(-1)?.headers ?? [];
  const byId = new Map(headers.map((header) => [header.column.id, header]));

  return (
    <div role="rowgroup" className={styles.head}>
      <div role="row" aria-rowindex={1} className={cx(styles.row, styles.headerRow)}>
        {layout.columns.map((entry) => {
          const { column } = entry;
          const header = byId.get(column.id);
          const className = cx(
            styles.cell,
            styles.headerCell,
            entry.pinned && styles.pinned,
            entry.edge && styles.pinnedEdge,
          );
          if (column.id === selectColumnId) {
            return (
              <div
                key={column.id}
                role="columnheader"
                className={cx(className, styles.selectCell)}
                style={entry.style}
                data-pinned={entry.pinned || undefined}
              >
                {selectable ? (
                  <input
                    type="checkbox"
                    className={styles.rowCheckbox}
                    data-header-select
                    checked={allSelected}
                    ref={(el) => {
                      if (el) el.indeterminate = someSelected && !allSelected;
                    }}
                    onChange={onToggleAll}
                    aria-label={t('dataTable.selectAll')}
                  />
                ) : null}
              </div>
            );
          }
          const label = column.columnDef.meta?.label ?? column.id;
          const sortIndex = column.getSortIndex();
          const direction = column.getIsSorted();
          const primary = sortIndex === 0 && direction !== false;
          const content = header ? flexRender(column.columnDef.header, header.getContext()) : label;
          return (
            <div
              key={column.id}
              role="columnheader"
              className={className}
              style={entry.style}
              data-pinned={entry.pinned || undefined}
              data-align={entry.align}
              data-sorted={direction || undefined}
              {...(primary
                ? { 'aria-sort': direction === 'asc' ? 'ascending' : 'descending' }
                : {})}
            >
              {column.getCanSort() ? (
                <button
                  type="button"
                  className={styles.sortButton}
                  aria-describedby={sortHintId}
                  onClick={column.getToggleSortingHandler()}
                  onKeyDown={(event) => {
                    resizeKeys(event, table, column, false);
                  }}
                >
                  <span className={styles.headerLabel}>{content}</span>
                  {direction ? (
                    <span className={styles.sortIndicator}>
                      <Icon icon={direction === 'asc' ? ArrowUp : ArrowDown} size={12} />
                      {multi ? (
                        <>
                          <span aria-hidden="true">{sortIndex + 1}</span>
                          <span className="ds-visually-hidden">
                            {t('dataTable.sortPriority', { index: sortIndex + 1 })}
                          </span>
                        </>
                      ) : null}
                    </span>
                  ) : (
                    <span className={styles.sortHover} aria-hidden="true">
                      <Icon icon={ArrowUpDown} size={12} />
                    </span>
                  )}
                </button>
              ) : (
                <span className={styles.headerLabel}>{content}</span>
              )}
              {column.columnDef.meta?.filter ? (
                <ColumnFilter
                  column={column}
                  isOpen={openFilter === column.id}
                  onOpenChange={(open) => {
                    onOpenFilterChange(open ? column.id : null);
                  }}
                />
              ) : null}
              {column.getCanResize() && header ? (
                <div
                  role="separator"
                  aria-orientation="vertical"
                  aria-label={t('dataTable.resize', { column: label })}
                  aria-valuenow={column.getSize()}
                  aria-valuemin={columnSizeLimits.min}
                  aria-valuemax={columnSizeLimits.max}
                  tabIndex={0}
                  className={styles.resizer}
                  data-resizing={column.getIsResizing() || undefined}
                  onMouseDown={header.getResizeHandler()}
                  onTouchStart={header.getResizeHandler()}
                  onDoubleClick={() => {
                    autosizeColumn(table, column);
                  }}
                  onKeyDown={(event) => {
                    resizeKeys(event, table, column, true);
                  }}
                />
              ) : null}
            </div>
          );
        })}
      </div>
    </div>
  );
}
