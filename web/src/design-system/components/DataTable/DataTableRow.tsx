import { flexRender, type Row } from '@tanstack/react-table';
import { ChevronRight, CircleAlert, Lock } from 'lucide-react';
import { memo, type CSSProperties, type ReactNode } from 'react';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import styles from './DataTable.module.css';
import { selectColumnId, type ColumnLayout } from './layout';
import type { DataTableRowState } from './types';

const noop = () => undefined;

export interface RowPosition {
  /** `aria-rowindex` (the header row is 1). */
  ariaRowIndex: number;
  itemKey: string;
  /** Roving tab stop: the cursor row (or the first row) has tabIndex 0. */
  tabbable: boolean;
  isCursor: boolean;
  /** translateY for virtualised rows; `null` in normal flow. */
  start: number | null;
  /** `aria-level` when the table is a treegrid (grouping). */
  level?: number;
}

function positionProps(position: RowPosition) {
  return {
    role: 'row',
    'aria-rowindex': position.ariaRowIndex,
    'data-item-key': position.itemKey,
    'data-cursor': position.isCursor || undefined,
    tabIndex: position.tabbable ? 0 : -1,
    ...(position.level === undefined ? {} : { 'aria-level': position.level }),
    style:
      position.start === null
        ? undefined
        : ({ transform: `translateY(${String(position.start)}px)` } satisfies CSSProperties),
  } as const;
}

function cellClass<TData>(layout: ColumnLayout<TData>) {
  return cx(styles.cell, layout.pinned && styles.pinned, layout.edge && styles.pinnedEdge);
}

export interface DataRowProps<TData> {
  row: Row<TData>;
  layout: readonly ColumnLayout<TData>[];
  /** Memo key: changes when columns, widths or pinning change. */
  layoutKey: string;
  position: RowPosition;
  selectable: boolean;
  isSelected: boolean;
  selectLabel: string;
  state: DataTableRowState | undefined;
  /** «Νέο» label for the new-row dot. */
  newLabel: string;
  variant: 'single' | 'queue';
}

function DataRowInner<TData>({
  row,
  layout,
  position,
  selectable,
  isSelected,
  selectLabel,
  state,
  newLabel,
  variant,
}: DataRowProps<TData>) {
  const locked = state?.lockedReason;
  const cells = row.getVisibleCells();
  const firstDataIndex = cells.findIndex((cell) => cell.column.id !== selectColumnId);
  return (
    <div
      {...positionProps(position)}
      className={cx(styles.row, position.start !== null && styles.virtualRow)}
      data-variant={variant}
      data-selected={isSelected || undefined}
      data-new={state?.isNew ? true : undefined}
      data-locked={locked ? true : undefined}
      data-error={state?.error ? true : undefined}
      {...(selectable ? { 'aria-selected': isSelected } : {})}
      {...(locked ? { 'aria-disabled': true } : {})}
    >
      {cells.map((cell, index) => {
        const entry = layout[index];
        if (!entry) return null;
        if (cell.column.id === selectColumnId) {
          return (
            <div
              key={cell.id}
              role="gridcell"
              className={cx(cellClass(entry), styles.selectCell)}
              style={entry.style}
              data-pinned={entry.pinned || undefined}
            >
              <input
                type="checkbox"
                className={styles.rowCheckbox}
                data-select
                tabIndex={-1}
                checked={isSelected}
                disabled={Boolean(locked)}
                onChange={noop}
                aria-label={selectLabel}
              />
            </div>
          );
        }
        const lead: ReactNode =
          index === firstDataIndex ? (
            <>
              {state?.isNew ? (
                <span className={styles.newDot}>
                  <span className="ds-visually-hidden">{newLabel}</span>
                </span>
              ) : null}
              {locked ? (
                <span className={styles.lockIcon} title={locked}>
                  <Icon icon={Lock} size={12} />
                  <span className="ds-visually-hidden">{locked}</span>
                </span>
              ) : null}
            </>
          ) : null;
        return (
          <div
            key={cell.id}
            role="gridcell"
            className={cellClass(entry)}
            data-align={entry.align}
            data-cell-type={cell.column.columnDef.meta?.cellType}
            data-pinned={entry.pinned || undefined}
            style={entry.style}
          >
            {lead}
            {flexRender(cell.column.columnDef.cell, cell.getContext())}
          </div>
        );
      })}
    </div>
  );
}

/** One data row: memoised; re-renders only when its data, selection, cursor, state or layout change. */
export const DataRow = memo(DataRowInner, (a, b) => {
  return (
    a.row === b.row &&
    a.layoutKey === b.layoutKey &&
    a.isSelected === b.isSelected &&
    a.state === b.state &&
    a.selectLabel === b.selectLabel &&
    a.newLabel === b.newLabel &&
    a.variant === b.variant &&
    a.selectable === b.selectable &&
    a.position.ariaRowIndex === b.position.ariaRowIndex &&
    a.position.isCursor === b.position.isCursor &&
    a.position.tabbable === b.position.tabbable &&
    a.position.start === b.position.start &&
    a.position.level === b.position.level &&
    a.position.itemKey === b.position.itemKey
  );
}) as typeof DataRowInner;

export interface SpanRowProps {
  position: RowPosition;
  kind: 'group' | 'message' | 'hiddenNormal' | 'empty';
  isExpanded?: boolean;
  children: ReactNode;
}

/** A row with one cell spanning every column (group header, error message, hidden-normal, empty states). */
export function SpanRow({ position, kind, isExpanded, children }: SpanRowProps) {
  return (
    <div
      {...positionProps(position)}
      className={cx(styles.row, styles.spanRow, position.start !== null && styles.virtualRow)}
      data-kind={kind}
      {...(isExpanded === undefined ? {} : { 'aria-expanded': isExpanded })}
    >
      <div role="gridcell" className={styles.spanCell}>
        {kind === 'group' ? (
          <span
            className={styles.groupChevron}
            data-expanded={isExpanded ? true : undefined}
            aria-hidden="true"
          >
            <Icon icon={ChevronRight} size={14} />
          </span>
        ) : null}
        {kind === 'message' ? (
          <span className={styles.messageIcon} aria-hidden="true">
            <Icon icon={CircleAlert} size={14} />
          </span>
        ) : null}
        {children}
      </div>
    </div>
  );
}

/** First-load skeleton row: header + 8 of these. */
export function SkeletonRow<TData>({
  layout,
  ariaRowIndex,
}: {
  layout: readonly ColumnLayout<TData>[];
  ariaRowIndex: number;
}) {
  return (
    <div role="row" aria-rowindex={ariaRowIndex} className={styles.row} data-skeleton>
      {layout.map((entry) => (
        <div
          key={entry.column.id}
          role="gridcell"
          className={cellClass(entry)}
          style={entry.style}
          data-pinned={entry.pinned || undefined}
        >
          <span className={styles.skeletonBlock} aria-hidden="true" />
        </div>
      ))}
    </div>
  );
}
