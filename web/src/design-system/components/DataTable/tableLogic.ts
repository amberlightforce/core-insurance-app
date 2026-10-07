import type { Column, FilterFn, Row, SortingFn, Table } from '@tanstack/react-table';

import { normalizeForSearch } from '../../../format/search';
import { queueRowHeights, tableRowHeights } from '../../tokens';
import type { DataTableDensity, DataTableRowState, DataTableRowVariant } from './types';

/** Greek collation (Part 2 §4.35): «ά» sorts with «α», «ΑΣΦ-2» before «ΑΣΦ-10». */
export const tableCollator = new Intl.Collator('el', { sensitivity: 'base', numeric: true });

function comparable(value: unknown): number | string | null {
  if (value === null || value === undefined || value === '') return null;
  if (typeof value === 'number') return value;
  if (typeof value === 'bigint') return Number(value);
  if (typeof value === 'boolean') return value ? 1 : 0;
  if (value instanceof Date) return value.getTime();
  if (typeof value === 'string') return value;
  return JSON.stringify(value);
}

/** Default sorting: numbers numerically, dates by time, text with the Greek collator. */
export const collateSort: SortingFn<unknown> = (a, b, columnId) => {
  const x = comparable(a.getValue(columnId));
  const y = comparable(b.getValue(columnId));
  if (x === null || y === null) return x === y ? 0 : x === null ? 1 : -1;
  if (typeof x === 'number' && typeof y === 'number') return x - y;
  return tableCollator.compare(String(x), String(y));
};

function searchable(value: unknown): string {
  if (value === null || value === undefined) return '';
  if (typeof value === 'string') return value;
  if (typeof value === 'number' || typeof value === 'bigint' || typeof value === 'boolean') {
    return String(value);
  }
  return '';
}

/** Accent- and case-insensitive «contains» («ζημια» matches «Ζημία»). */
export function includesNormalized(haystack: unknown, needle: string): boolean {
  const query = normalizeForSearch(needle);
  if (!query) return true;
  return normalizeForSearch(searchable(haystack)).includes(query);
}

export const textContainsFilter: FilterFn<unknown> = (row, columnId, value) =>
  includesNormalized(row.getValue(columnId), typeof value === 'string' ? value : '');
textContainsFilter.autoRemove = (value) => typeof value !== 'string' || value.trim() === '';

export const enumFilter: FilterFn<unknown> = (row, columnId, value) => {
  if (!Array.isArray(value) || value.length === 0) return true;
  return (value as unknown[]).includes(searchable(row.getValue(columnId)));
};
enumFilter.autoRemove = (value) => !Array.isArray(value) || value.length === 0;

export const globalSearchFilter: FilterFn<unknown> = (row, columnId, value) =>
  includesNormalized(row.getValue(columnId), typeof value === 'string' ? value : '');

/** Single-key shortcuts never fire while the user types (WCAG 2.1.4, Part 2 §4.35). */
export function isTextEntry(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  if (target.isContentEditable) return true;
  if (target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) return true;
  if (target instanceof HTMLInputElement) {
    return !['checkbox', 'radio', 'button', 'submit', 'reset', 'range', 'color'].includes(
      target.type,
    );
  }
  return target.getAttribute('role') === 'textbox' || target.getAttribute('role') === 'searchbox';
}

/** Fixed row heights per density (O(1) virtualisation); must match the CSS tokens. */
export function rowHeightFor(
  variant: DataTableRowVariant,
  density: DataTableDensity,
  touch: boolean,
): number {
  const table = touch ? tableRowHeights.touch : tableRowHeights[density];
  if (variant !== 'queue') return table;
  if (touch) return queueRowHeights.touch;
  return density === 'comfortable' ? queueRowHeights.comfortable : queueRowHeights.compact;
}

/** Items the body renders: data rows, group rows and the extra rows around them. */
export type TableItem<TData> =
  | { kind: 'row'; key: string; row: Row<TData> }
  | { kind: 'group'; key: string; row: Row<TData> }
  | { kind: 'message'; key: string; row: Row<TData>; message: string; onRetry?: () => void }
  | { kind: 'hiddenNormal'; key: string };

export function buildItems<TData>(
  rows: readonly Row<TData>[],
  getRowState: ((row: TData) => DataTableRowState | undefined) | undefined,
  hiddenNormalCount: number | undefined,
): TableItem<TData>[] {
  const items: TableItem<TData>[] = [];
  for (const row of rows) {
    if (row.getIsGrouped()) {
      items.push({ kind: 'group', key: row.id, row });
      continue;
    }
    items.push({ kind: 'row', key: row.id, row });
    const error = getRowState?.(row.original)?.error;
    if (error) {
      items.push({
        kind: 'message',
        key: `${row.id}::message`,
        row,
        message: error.message,
        ...(error.onRetry ? { onRetry: error.onRetry } : {}),
      });
    }
  }
  if (hiddenNormalCount !== undefined && hiddenNormalCount > 0) {
    items.push({ kind: 'hiddenNormal', key: '__hiddenNormal' });
  }
  return items;
}

/**
 * Autosize-lite: estimates a column width from the longest of its header and first 200 values
 * (no DOM measuring), clamped to 64–480 px (Part 2 §4.35 resizing).
 */
export function estimateColumnWidth(
  header: string,
  values: readonly string[],
  mono = false,
): number {
  const charWidth = mono ? 8 : 7;
  const longest = values
    .slice(0, 200)
    .reduce((max, value) => Math.max(max, Array.from(value).length), Array.from(header).length);
  return Math.min(480, Math.max(64, Math.ceil(longest * charWidth + 32)));
}

export const columnSizeLimits = { min: 64, max: 640, step: 16 } as const;

/** Display string of a cell value for autosize and the card list fallback. */
export function displayText(value: unknown): string {
  return searchable(value);
}

/** Resizes a column by `delta` px within 64–640 (Ctrl+Alt+←/→ uses 16 px steps). */
export function resizeColumnBy<TData>(table: Table<TData>, column: Column<TData>, delta: number) {
  const next = Math.min(
    columnSizeLimits.max,
    Math.max(columnSizeLimits.min, column.getSize() + delta),
  );
  table.setColumnSizing((previous) => ({ ...previous, [column.id]: next }));
}

/** Double-click on the resize handle: autosize-lite from the first 200 rows (max 480 px). */
export function autosizeColumn<TData>(table: Table<TData>, column: Column<TData>) {
  const values = table
    .getCoreRowModel()
    .rows.slice(0, 200)
    .map((row) => displayText(row.getValue(column.id)));
  const width = estimateColumnWidth(
    column.columnDef.meta?.label ?? column.id,
    values,
    column.columnDef.meta?.cellType === 'identifier',
  );
  table.setColumnSizing((previous) => ({ ...previous, [column.id]: width }));
}
