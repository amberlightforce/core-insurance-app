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
  const money = (key: string, value: string): string | null =>
    /(amount|reserve|paid|incurred)/i.test(key) && /^-?\d+(\.\d+)?$/.test(value)
      ? formatMoney({ amount: value, currency: 'EUR' })
      : null;
  // Nested values (a set's lines, authority checks) read as «key: value; key: value»; amounts are formatted.
  const show = (value: unknown, key = ''): string | null => {
    if (value === null || value === undefined) return null;
    if (isMoney(value)) return formatMoney(value);
    if (typeof value === 'string') return money(key, value) ?? value;
    if (typeof value === 'boolean') return value ? labels.yes : labels.no;
    if (typeof value === 'number') return String(value);
    if (Array.isArray(value)) return value.map((item) => show(item, key)).join(' | ');
    if (isRecord(value))
      return Object.entries(value)
        .map(([k, v]) => `${k}: ${show(v, k) ?? '—'}`)
        .join('; ');
    return JSON.stringify(value);
  };
  // Identifiers and the content hash are shown in the request summary; they add nothing to the change itself.
  const hidden = new Set(['setId', 'claimId', 'contentHash']);
  return Object.entries(diff)
    .filter(([key]) => !hidden.has(key))
    .map(([key, raw]) => {
      if (isRecord(raw) && ('from' in raw || 'to' in raw) && Object.keys(raw).length <= 2) {
        return { id: key, label: key, from: show(raw.from), to: show(raw.to), value: null };
      }
      return { id: key, label: key, from: null, to: null, value: show(raw, key) };
    });
}
