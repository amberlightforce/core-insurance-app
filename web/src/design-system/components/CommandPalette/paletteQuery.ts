import type { LucideIcon } from 'lucide-react';

import { matchesQuery, normalizeForSearch, type MatchRange } from '../../../format/search';

/** Palette groups in display order (Part 2 §4.36): Ενέργειες / Μετάβαση σε / Εγγραφές / Πρόσφατα. */
export const paletteGroups = ['actions', 'navigation', 'records', 'recent'] as const;
export type PaletteGroup = (typeof paletteGroups)[number];

/** Record types the query prefixes filter on. */
export type PaletteRecordKind = 'policy' | 'claim' | 'customer';

/** A scope chip: a record type, actions only (`>`), or identifiers (`#`). */
export type PaletteScope = PaletteRecordKind | 'actions' | 'id';

export interface PaletteItem {
  id: string;
  group: PaletteGroup;
  title: string;
  /** Secondary line: mono ID · status · fact. */
  subtitle?: string;
  /** Business identifier matched by `#` queries, e.g. «ΑΣΦ-2026-004471». */
  code?: string;
  kind?: PaletteRecordKind;
  icon?: LucideIcon;
  /** Shortcut chip, e.g. `G P`. */
  shortcut?: string;
  /** Extra search terms (synonyms, English names). */
  keywords?: string[];
  onAction: () => void;
}

export interface PaletteHit {
  item: PaletteItem;
  /** Ranges in `item.title` to highlight (empty when the match was in another field). */
  ranges: MatchRange[];
}

export interface PaletteSection {
  group: PaletteGroup;
  hits: PaletteHit[];
}

const prefixes: readonly (readonly [string, PaletteScope])[] = [
  ['ασφ:', 'policy'],
  ['pol:', 'policy'],
  ['ζημ:', 'claim'],
  ['clm:', 'claim'],
  ['πελ:', 'customer'],
  ['cus:', 'customer'],
  ['>', 'actions'],
  ['#', 'id'],
];

/**
 * Reads a scope prefix at the start of the input (`ασφ:`/`pol:`, `ζημ:`/`clm:`, `πελ:`/`cus:`, `>`, `#`),
 * accent- and case-insensitively. Returns the scope and the remaining text.
 */
export function parsePrefix(input: string): { scope: PaletteScope | null; rest: string } {
  const trimmed = input.trimStart();
  const folded = normalizeForSearch(trimmed.slice(0, 4));
  for (const [prefix, scope] of prefixes) {
    if (folded.startsWith(prefix)) {
      return { scope, rest: trimmed.slice(prefix.length).trimStart() };
    }
  }
  return { scope: null, rest: input };
}

const scopeCycle: readonly (PaletteScope | null)[] = [
  null,
  'policy',
  'claim',
  'customer',
  'actions',
];

/** Tab cycles the type filter: none → policies → claims → customers → actions → none. */
export function nextScope(scope: PaletteScope | null): PaletteScope | null {
  const index = scopeCycle.indexOf(scope);
  return scopeCycle[(index + 1) % scopeCycle.length] ?? null;
}

const maxRecent = 8;

function inScope(item: PaletteItem, scope: PaletteScope | null): boolean {
  if (scope === null) return item.group !== 'recent';
  if (scope === 'actions') return item.group === 'actions';
  if (scope === 'id') return item.group === 'records' && item.code !== undefined;
  return item.group === 'records' && item.kind === scope;
}

function match(item: PaletteItem, query: string, scope: PaletteScope | null): PaletteHit | null {
  if (scope === 'id') {
    return matchesQuery(item.code ?? '', query) ? { item, ranges: [] } : null;
  }
  const title = matchesQuery(item.title, query);
  if (title) return { item, ranges: title.ranges };
  const others = [item.subtitle, item.code, ...(item.keywords ?? [])];
  return others.some((text) => text !== undefined && matchesQuery(text, query) !== null)
    ? { item, ranges: [] }
    : null;
}

/**
 * Groups and filters palette items. An empty query without a scope shows the recent items; otherwise every
 * in-scope item that matches (accent-insensitive, Greeklish-tolerant) is listed per group, local items
 * before remote records, without duplicates.
 */
export function buildSections(
  items: readonly PaletteItem[],
  remote: readonly PaletteItem[],
  query: string,
  scope: PaletteScope | null,
): PaletteSection[] {
  const q = query.trim();
  if (q === '' && scope === null) {
    const recent = items.filter((item) => item.group === 'recent').slice(0, maxRecent);
    return recent.length > 0
      ? [{ group: 'recent', hits: recent.map((item) => ({ item, ranges: [] })) }]
      : [];
  }
  const seen = new Set<string>();
  const hits: PaletteHit[] = [];
  for (const item of [...items, ...remote]) {
    if (seen.has(item.id) || !inScope(item, scope)) continue;
    const hit = q === '' ? { item, ranges: [] } : match(item, q, scope);
    if (hit) {
      seen.add(item.id);
      hits.push(hit);
    }
  }
  return paletteGroups
    .map((group) => ({ group, hits: hits.filter((hit) => hit.item.group === group) }))
    .filter((section) => section.hits.length > 0);
}
