import type { Column, Table } from '@tanstack/react-table';
import type { CSSProperties } from 'react';

export const selectColumnId = '__select';
export const actionsColumnId = '__actions';

export interface ColumnLayout<TData> {
  column: Column<TData>;
  pinned: false | 'left' | 'right';
  /** Sticky offset for pinned columns (logical inset). */
  style: CSSProperties | undefined;
  /** The pinned column next to the scrolling area; it carries the pinned shadow when scrolled (MI-22). */
  edge: boolean;
  align: 'start' | 'end' | 'center';
}

export interface TableLayout<TData> {
  columns: ColumnLayout<TData>[];
  /** `grid-template-columns` for every row. */
  template: string;
  /** Sum of the column widths (min inline size of a row). */
  width: number;
  /** Changes whenever anything a row renders from the layout changes (memo key). */
  key: string;
}

/** Visible columns in render order (left pinned, centre, right pinned) with sticky offsets. */
export function computeLayout<TData>(table: Table<TData>): TableLayout<TData> {
  const left = table.getLeftVisibleLeafColumns();
  const center = table.getCenterVisibleLeafColumns();
  const right = table.getRightVisibleLeafColumns();
  const lastCenter = center.at(-1);
  const columns: ColumnLayout<TData>[] = [];
  const tracks: string[] = [];
  let width = 0;
  for (const column of [...left, ...center, ...right]) {
    const pinned = column.getIsPinned();
    const size = column.getSize();
    width += size;
    tracks.push(column === lastCenter ? `minmax(${String(size)}px, 1fr)` : `${String(size)}px`);
    columns.push({
      column,
      pinned,
      style:
        pinned === 'left'
          ? { insetInlineStart: column.getStart('left') }
          : pinned === 'right'
            ? { insetInlineEnd: column.getAfter('right') }
            : undefined,
      edge:
        (pinned === 'left' && column.getIsLastColumn('left')) ||
        (pinned === 'right' && column.getIsFirstColumn('right')),
      align: column.columnDef.meta?.align ?? 'start',
    });
  }
  const template = tracks.join(' ');
  const key = `${template}|${columns.map((c) => `${c.column.id}:${c.pinned || ''}`).join(',')}`;
  return { columns, template, width, key };
}
