import type { CellContext, ColumnDef, ColumnMeta } from '@tanstack/react-table';
import { createElement, type ReactNode } from 'react';

import type { DateInput } from '../../../format/dates';
import type { Numeric } from '../../../format/numbers';
import {
  BooleanCell,
  DateCell,
  IdentifierCell,
  MoneyCell,
  PercentCell,
  PriorityCell,
  QueueCell,
  TextCell,
} from './cells';
import './types';

/**
 * Column helpers: each returns a TanStack `ColumnDef` with the design-system cell renderer, alignment and
 * metadata. The renderer is created once per column (no per-cell closures in the hot path).
 */
export type DataColumn<Row> = ColumnDef<Row>;

interface CommonOptions<Row> {
  /** Width in px (64–640). */
  size?: number;
  enableSorting?: boolean;
  enableHiding?: boolean;
  /** Extra metadata (filter kind, enum options…). */
  meta?: ColumnMeta<Row, unknown>;
}

function base<Row>(
  id: string,
  header: string,
  accessorFn: (row: Row) => unknown,
  meta: ColumnMeta<Row, unknown>,
  options: CommonOptions<Row>,
  cell: (context: CellContext<Row, unknown>) => ReactNode,
): DataColumn<Row> {
  return {
    id,
    header,
    accessorFn,
    cell,
    meta: { label: header, ...meta, ...options.meta },
    ...(options.size !== undefined ? { size: options.size } : {}),
    ...(options.enableSorting !== undefined ? { enableSorting: options.enableSorting } : {}),
    ...(options.enableHiding !== undefined ? { enableHiding: options.enableHiding } : {}),
  };
}

const asString = (value: unknown): string | null =>
  typeof value === 'string' ? value : typeof value === 'number' ? String(value) : null;

export function textColumn<Row>(
  id: string,
  header: string,
  accessor: (row: Row) => string | null | undefined,
  options: CommonOptions<Row> = {},
): DataColumn<Row> {
  return base(id, header, accessor, { cellType: 'text', align: 'start' }, options, (context) =>
    createElement(TextCell, { value: asString(context.getValue()) }),
  );
}

export function identifierColumn<Row>(
  id: string,
  header: string,
  accessor: (row: Row) => string | null | undefined,
  options: CommonOptions<Row> = {},
): DataColumn<Row> {
  return base(
    id,
    header,
    accessor,
    { cellType: 'identifier', align: 'start' },
    options,
    (context) => createElement(IdentifierCell, { value: asString(context.getValue()) }),
  );
}

export function moneyColumn<Row>(
  id: string,
  header: string,
  accessor: (row: Row) => Numeric | null | undefined,
  options: CommonOptions<Row> & {
    currency?: string;
    /** Marks adverse amounts (default: negative values). */
    isAdverse?: (row: Row) => boolean;
  } = {},
): DataColumn<Row> {
  const { currency, isAdverse } = options;
  return base(id, header, accessor, { cellType: 'money', align: 'end' }, options, (context) =>
    createElement(MoneyCell, {
      value: context.getValue() as Numeric | null | undefined,
      ...(currency ? { currency } : {}),
      ...(isAdverse ? { adverse: isAdverse(context.row.original) } : {}),
    }),
  );
}

export function percentColumn<Row>(
  id: string,
  header: string,
  accessor: (row: Row) => Numeric | null | undefined,
  options: CommonOptions<Row> & { fractionDigits?: number } = {},
): DataColumn<Row> {
  const { fractionDigits } = options;
  return base(id, header, accessor, { cellType: 'percent', align: 'end' }, options, (context) =>
    createElement(PercentCell, {
      value: context.getValue() as Numeric | null | undefined,
      ...(fractionDigits !== undefined ? { fractionDigits } : {}),
    }),
  );
}

export function dateColumn<Row>(
  id: string,
  header: string,
  accessor: (row: Row) => DateInput | null | undefined,
  options: CommonOptions<Row> = {},
): DataColumn<Row> {
  // Sort by time: Date values compare numerically in the default sorting function.
  return base(
    id,
    header,
    (row) => {
      const value = accessor(row);
      return value === null || value === undefined ? null : toSortableDate(value);
    },
    { cellType: 'date', align: 'start' },
    options,
    (context) => createElement(DateCell, { value: accessor(context.row.original) ?? null }),
  );
}

function toSortableDate(value: DateInput): Date {
  if (value instanceof Date) return value;
  if (typeof value === 'number') return new Date(value);
  if (typeof value === 'string')
    return new Date(/^\d{4}-\d{2}-\d{2}$/.test(value) ? `${value}T12:00:00Z` : value);
  return new Date(Date.UTC(value.year, value.month - 1, value.day, 12));
}

/**
 * Status column: the accessor returns the sortable/filterable value (e.g. the state key); `render` draws
 * it, usually `<StatusPill …/>`. Pass `enumOptions` to offer the checklist filter.
 */
export function statusColumn<Row>(
  id: string,
  header: string,
  accessor: (row: Row) => string | null | undefined,
  render: (row: Row) => ReactNode,
  options: CommonOptions<Row> = {},
): DataColumn<Row> {
  return base(id, header, accessor, { cellType: 'status', align: 'start' }, options, (context) =>
    render(context.row.original),
  );
}

export function priorityColumn<Row>(
  id: string,
  header: string,
  accessor: (row: Row) => number | null | undefined,
  options: CommonOptions<Row> & { max?: number } = {},
): DataColumn<Row> {
  const { max } = options;
  return base(id, header, accessor, { cellType: 'priority', align: 'start' }, options, (context) =>
    createElement(PriorityCell, {
      value: context.getValue() as number | null | undefined,
      ...(max !== undefined ? { max } : {}),
    }),
  );
}

export function booleanColumn<Row>(
  id: string,
  header: string,
  accessor: (row: Row) => boolean | null | undefined,
  options: CommonOptions<Row> = {},
): DataColumn<Row> {
  return base(id, header, accessor, { cellType: 'boolean', align: 'center' }, options, (context) =>
    createElement(BooleanCell, { value: context.getValue() as boolean | null | undefined }),
  );
}

/**
 * Two-line queue column (v3 queue row): primary line + mono id · fact. Sorting and search use
 * «primary id fact», so the primary line leads.
 */
export function queueColumn<Row>(
  id: string,
  header: string,
  parts: (row: Row) => { primary: string; id?: string; fact?: string },
  options: CommonOptions<Row> = {},
): DataColumn<Row> {
  return base(
    id,
    header,
    (row) => {
      const p = parts(row);
      return [p.primary, p.id, p.fact].filter(Boolean).join(' ');
    },
    { cellType: 'queue', align: 'start' },
    options,
    (context) => createElement(QueueCell, parts(context.row.original)),
  );
}

/** Actions column, pinned right: ⋯ and 1–2 quick actions (render prop). */
export function actionsColumn<Row>(
  header: string,
  render: (row: Row) => ReactNode,
  options: CommonOptions<Row> = {},
): DataColumn<Row> {
  return {
    ...base(
      '__actions',
      header,
      () => null,
      { cellType: 'actions', align: 'end' },
      options,
      (context) => render(context.row.original),
    ),
    enableSorting: false,
    enableHiding: false,
    enableGlobalFilter: false,
  };
}
