import type { RowData } from '@tanstack/react-table';
import type { LucideIcon } from 'lucide-react';

/** Cell kinds (Part 2 §4.35 cell table); they drive alignment and the autosize estimate. */
export type DataTableCellType =
  | 'text'
  | 'identifier'
  | 'money'
  | 'percent'
  | 'date'
  | 'status'
  | 'priority'
  | 'boolean'
  | 'queue'
  | 'actions';

export interface EnumFilterOption {
  value: string;
  label: string;
}

declare module '@tanstack/react-table' {
  // Merged with TanStack's empty ColumnMeta; the type parameters must match its declaration.
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  interface ColumnMeta<TData extends RowData, TValue> {
    /** Plain-text header label for menus, filters, chips and accessible names. */
    label?: string;
    align?: 'start' | 'end' | 'center';
    cellType?: DataTableCellType;
    /** Column filter offered in the header: `text` (contains) or `enum` (checklist). */
    filter?: 'text' | 'enum';
    enumOptions?: readonly EnumFilterOption[];
  }
}

/** Per-row states (Part 2 §4.35 row states). */
export interface DataTableRowState {
  /** Disabled/locked row: `text.tertiary`, lock and the reason; not selectable. */
  lockedReason?: string;
  /** Error row: 3 px danger bar and an inline message row with «Επανάληψη». */
  error?: { message: string; onRetry?: () => void };
  /** Live insert: brand flash (MI-48) and a «Νέο» dot while true. */
  isNew?: boolean;
}

export interface DataTableBulkAction {
  id: string;
  label: string;
  icon?: LucideIcon;
  onAction: (selectedIds: string[]) => void;
}

export interface DataTableError {
  /** Defaults to «Δεν ήταν δυνατή η φόρτωση των εγγραφών.» */
  message?: string;
  onRetry?: () => void;
}

export interface DataTableFooterTotal {
  /** e.g. «Σύνολο ασφαλίστρων» */
  label: string;
  value: number | string;
  currency?: string;
}

export type DataTableRowVariant = 'single' | 'queue';
export type DataTableDensity = 'compact' | 'comfortable';
