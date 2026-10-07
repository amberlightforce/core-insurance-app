import type { LucideIcon } from 'lucide-react';

/** `default` brand chip; `locked` ABAC filter (lock, no ×); `invalid` saved filter whose field is gone; `readOnly` neutral, no ×. */
export type FilterChipState = 'default' | 'locked' | 'invalid' | 'readOnly';

export interface FilterChipItem {
  id: string;
  /** Field label, e.g. «Κατάσταση». Omit for a free-text chip. */
  field?: string;
  /** Selected values, e.g. «Σε ισχύ», «Σε εκκρεμή ακύρωση», or one free-text value. */
  values: readonly string[];
  state?: FilterChipState;
  /** Optional leading icon (ignored for locked and invalid chips, which have their own). */
  icon?: LucideIcon;
}

/** Values longer than this are truncated with «…» and shown in full in a tooltip (Part 2 §4.14). */
export const chipValueLimit = 32;

export function chipText(item: FilterChipItem): {
  full: string;
  shown: string;
  truncated: boolean;
} {
  const full = item.values.join(', ');
  const chars = Array.from(full);
  if (chars.length <= chipValueLimit) return { full, shown: full, truncated: false };
  return { full, shown: `${chars.slice(0, chipValueLimit).join('').trimEnd()}…`, truncated: true };
}

export function isRemovable(item: FilterChipItem): boolean {
  return item.state !== 'locked' && item.state !== 'readOnly';
}

export function isEditable(item: FilterChipItem): boolean {
  return item.state !== 'locked' && item.state !== 'readOnly';
}
