import type { ApprovalView } from '../../api/types';

interface MoneyLike {
  amount: string;
  currency: string;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isMoney(value: unknown): value is MoneyLike {
  return (
    isRecord(value) &&
    typeof value.amount === 'string' &&
    typeof value.currency === 'string' &&
    Object.keys(value).length === 2
  );
}

export interface DiffRow {
  id: string;
  label: string;
  from: string | null;
  to: string | null;
  value: string | null;
}

/** Approval type code → i18n key (the dot of «CLM.PAYMENT» is the key separator, so it becomes «_»). */
export function approvalTypeKey(type: string): string {
  return `approvals.type.${type.replaceAll('.', '_')}`;
}

/**
 * Rows of the field-level diff the maker sent (an open object, no P2 values): `{ from, to }` members become a
 * before/after pair, money members are formatted, anything else is shown as text.
 */
export function diffRows(
  diff: ApprovalView['diff'],
  formatMoney: (money: MoneyLike) => string,
  labels: { yes: string; no: string },
): DiffRow[] {
  if (!diff) return [];
  const show = (value: unknown): string | null => {
    if (value === null || value === undefined) return null;
    if (isMoney(value)) return formatMoney(value);
    if (typeof value === 'string') return value;
    if (typeof value === 'boolean') return value ? labels.yes : labels.no;
    return JSON.stringify(value);
  };
  return Object.entries(diff).map(([key, raw]) => {
    if (isRecord(raw) && ('from' in raw || 'to' in raw) && Object.keys(raw).length <= 2) {
      return { id: key, label: key, from: show(raw.from), to: show(raw.to), value: null };
    }
    return { id: key, label: key, from: null, to: null, value: show(raw) };
  });
}
