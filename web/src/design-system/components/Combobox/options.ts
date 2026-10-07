import type { ReactNode } from 'react';
import type { Key } from 'react-aria-components';

import { matchesQuery, searchItems, type MatchRange } from '../../../format/search';

/** An option for Combobox and MultiCombobox. */
export interface ComboboxOption {
  id: Key;
  /** Primary text; also what the input shows once selected. */
  label: string;
  /** Secondary line that disambiguates namesakes (birth year, «ΑΦΜ •••789», town). Searchable. */
  caption?: string;
  /** Right-aligned identifier, shown in mono («ΑΣΦ-2026-004471»). Searchable. */
  meta?: string;
  /** Right-aligned custom content (for example a status pill); replaces the `meta` display. */
  metaSlot?: ReactNode;
  /** Section header the option is grouped under. */
  group?: string;
  isDisabled?: boolean;
}

export interface OptionHit<T extends ComboboxOption> {
  item: T;
  labelRanges?: readonly MatchRange[];
  captionRanges?: readonly MatchRange[];
  metaRanges?: readonly MatchRange[];
}

export interface OptionSection<T extends ComboboxOption> {
  key: string;
  title: string | undefined;
  hits: OptionHit<T>[];
}

/** Keys of the special rows (create, retry); never valid option ids. */
export const CREATE_KEY = '__ds-combobox-create__';
export const RETRY_KEY = '__ds-combobox-retry__';

function searchableTexts(item: ComboboxOption): string[] {
  return [item.label, item.caption ?? '', item.meta ?? ''];
}

function hitFor<T extends ComboboxOption>(item: T, field: number, ranges: readonly MatchRange[]) {
  const hit: OptionHit<T> = { item };
  if (field === 0) hit.labelRanges = ranges;
  else if (field === 1) hit.captionRanges = ranges;
  else hit.metaRanges = ranges;
  return hit;
}

/** Filters and ranks static options with the Greek matcher (Greeklish, accents, IDs). */
export function filterOptions<T extends ComboboxOption>(
  items: readonly T[],
  query: string,
): OptionHit<T>[] {
  return searchItems(items, query, searchableTexts).map(({ item, match, field }) =>
    hitFor(item, field, match.ranges),
  );
}

/** Server-filtered options: keep the server's order, add highlight ranges where the query matches. */
export function highlightOptions<T extends ComboboxOption>(
  items: readonly T[],
  query: string,
): OptionHit<T>[] {
  return items.map((item) => {
    const texts = searchableTexts(item);
    for (let field = 0; field < texts.length; field++) {
      const match = matchesQuery(texts[field] ?? '', query);
      if (match && match.ranges.length > 0) return hitFor(item, field, match.ranges);
    }
    return { item };
  });
}

/** Groups hits by `group`, in order of first appearance; ungrouped options come first, without a header. */
export function groupOptions<T extends ComboboxOption>(
  hits: readonly OptionHit<T>[],
): OptionSection<T>[] {
  const sections = new Map<string, OptionSection<T>>();
  for (const hit of hits) {
    const title = hit.item.group;
    const key = title ?? '';
    let section = sections.get(key);
    if (!section) {
      section = { key, title, hits: [] };
      sections.set(key, section);
    }
    section.hits.push(hit);
  }
  const list = [...sections.values()];
  return list.sort((a, b) => (a.title === undefined ? -1 : b.title === undefined ? 1 : 0));
}
