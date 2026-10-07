export interface BreadcrumbItem {
  id: string;
  /** Consumer content, already translated (record titles, module names). */
  label: string;
  href?: string;
}

/**
 * With more than `max` items, keeps the first and the last two and collapses the middle into a ⋯ menu.
 * `max` below 3 is treated as 3 (first, ⋯, current).
 */
export function collapseBreadcrumbs<T extends BreadcrumbItem>(
  items: readonly T[],
  max = 4,
): { head: T[]; collapsed: T[]; tail: T[] } {
  if (items.length <= max) return { head: [], collapsed: [], tail: [...items] };
  const tailCount = Math.max(1, Math.min(2, max - 2));
  return {
    head: items.slice(0, 1),
    collapsed: items.slice(1, items.length - tailCount),
    tail: items.slice(items.length - tailCount),
  };
}
