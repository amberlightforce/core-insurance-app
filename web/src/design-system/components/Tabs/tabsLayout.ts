/** A tab definition for `<Tabs>`. Labels are consumer content (already translated). */
export interface TabItem {
  id: string;
  label: string;
  /** Count badge (neutral), e.g. the number of claims. */
  count?: number;
  /** Error dot; the count is part of the accessible name («Καλύψεις, 2 σφάλματα»). */
  errorCount?: number;
  /** Disabled with a reason: stays focusable with `aria-disabled`, the reason is a tooltip and description. */
  disabledReason?: string;
  /** Disabled without a reason (leaves the keyboard order). */
  isDisabled?: boolean;
}

export function isTabSelectable(item: TabItem): boolean {
  return !item.isDisabled && !item.disabledReason;
}

/**
 * Splits tabs into the visible strip and the «Περισσότερα» menu. The selected tab is always visible: when
 * it would overflow it swaps with the last visible tab (Part 2 §4.11).
 */
export function splitTabs(
  items: readonly TabItem[],
  visibleCount: number,
  selectedId: string | null,
): { visible: TabItem[]; overflow: TabItem[] } {
  const count = Math.max(1, Math.min(items.length, visibleCount));
  if (count >= items.length) return { visible: [...items], overflow: [] };
  const visible = items.slice(0, count);
  const selectedIndex = items.findIndex((item) => item.id === selectedId);
  if (selectedIndex >= count) {
    const selected = items[selectedIndex];
    if (selected) visible[count - 1] = selected;
  }
  const shown = new Set(visible.map((item) => item.id));
  return { visible, overflow: items.filter((item) => !shown.has(item.id)) };
}

/**
 * How many tabs fit in `available` px, given each tab's measured width and the width of the
 * «Περισσότερα» button (reserved only when something overflows).
 */
export function fitTabs(widths: readonly number[], available: number, moreWidth: number): number {
  const total = widths.reduce((sum, w) => sum + w, 0);
  if (total <= available) return widths.length;
  let used = 0;
  let fit = 0;
  for (const width of widths) {
    if (used + width > available - moreWidth) break;
    used += width;
    fit += 1;
  }
  return Math.max(1, fit);
}

/** The next selectable tab in `direction` (wrapping), for Ctrl+PgUp/PgDn. */
export function adjacentTab(
  items: readonly TabItem[],
  currentId: string | null,
  direction: 1 | -1,
): TabItem | undefined {
  const selectable = items.filter(isTabSelectable);
  if (selectable.length === 0) return undefined;
  const index = selectable.findIndex((item) => item.id === currentId);
  const next = (index + direction + selectable.length) % selectable.length;
  return selectable[index === -1 ? 0 : next];
}
