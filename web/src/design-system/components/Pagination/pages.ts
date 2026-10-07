export type PageSlot = { kind: 'page'; page: number } | { kind: 'gap'; key: string };

/**
 * Page buttons with gaps: always the first and last page, the current page ±1, and «…» for the rest,
 * at most 7 slots (1 … 4 5 6 … 97).
 */
export function pageSlots(current: number, pageCount: number): PageSlot[] {
  if (pageCount <= 7) {
    return Array.from({ length: pageCount }, (_, i) => ({ kind: 'page', page: i + 1 }));
  }
  const pages = new Set([1, pageCount, current - 1, current, current + 1]);
  if (current <= 3) [2, 3, 4].forEach((p) => pages.add(p));
  if (current >= pageCount - 2)
    [pageCount - 3, pageCount - 2, pageCount - 1].forEach((p) => pages.add(p));
  const sorted = [...pages].filter((p) => p >= 1 && p <= pageCount).sort((a, b) => a - b);
  const slots: PageSlot[] = [];
  let previous = 0;
  for (const page of sorted) {
    if (page - previous > 1) slots.push({ kind: 'gap', key: `gap-${String(page)}` });
    slots.push({ kind: 'page', page });
    previous = page;
  }
  return slots;
}

export function pageCountFor(total: number, pageSize: number): number {
  return Math.max(1, Math.ceil(total / Math.max(1, pageSize)));
}

/** First and last record numbers on a page (1-based, inclusive); 0–0 when empty. */
export function pageRange(page: number, pageSize: number, total: number): [number, number] {
  if (total <= 0) return [0, 0];
  const start = (page - 1) * pageSize + 1;
  return [Math.min(start, total), Math.min(page * pageSize, total)];
}
