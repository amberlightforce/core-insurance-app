import type { LucideIcon } from 'lucide-react';

import type { RegionFormat } from '../../preferences';
import type { StatusFamily } from '../../tokens';

export type TimelineCategory = 'transactions' | 'documents' | 'communication' | 'fields' | 'ai';
export type TimelineFilter = 'all' | TimelineCategory;

export type TimelineActor =
  | { kind: 'user'; id: string; name: string; initials?: string }
  | { kind: 'system' }
  /** An AI agent acting for a person: «Πράκτορας ΤΝ για Μ. Π.». */
  | { kind: 'agent'; onBehalfOf: string };

export interface TimelineDiff {
  field: string;
  /** Already formatted (dates dd/MM/yyyy, money el-GR). */
  from: string;
  to: string;
}

export interface TimelineEvent {
  id: string;
  at: Date;
  actor: TimelineActor;
  /** The verb phrase: «άλλαξε», «εξέδωσε», «επισύναψε». */
  action: string;
  object?: { label: string; href?: string; onPress?: () => void };
  category: TimelineCategory;
  /** Dot colour (status family). */
  family?: StatusFamily;
  /** Key events get a 20 px icon node instead of the 8 px dot. */
  icon?: LucideIcon;
  diffs?: TimelineDiff[];
  trace?: { label: string; href?: string };
  /** Just arrived (MI-48 live insert). */
  isNew?: boolean;
}

export interface TimelineEntrySingle {
  type: 'event';
  event: TimelineEvent;
}

export interface TimelineEntryGroup {
  type: 'group';
  id: string;
  actor: Extract<TimelineActor, { kind: 'user' }>;
  events: TimelineEvent[];
}

export type TimelineEntry = TimelineEntrySingle | TimelineEntryGroup;

export interface TimelineDay {
  key: string;
  date: Date;
  entries: TimelineEntry[];
}

const COLLAPSE_WINDOW_MS = 5 * 60 * 1000;

function dayKey(date: Date): string {
  return `${String(date.getFullYear())}-${String(date.getMonth() + 1)}-${String(date.getDate())}`;
}

function sameEditor(a: TimelineEvent, b: TimelineEvent): boolean {
  return (
    a.actor.kind === 'user' &&
    b.actor.kind === 'user' &&
    a.actor.id === b.actor.id &&
    a.category === 'fields' &&
    b.category === 'fields'
  );
}

/**
 * Groups events (newest first) by calendar day and collapses runs of field edits by the same person within
 * 5 minutes of each other («έκανε 6 αλλαγές»).
 */
export function groupTimeline(events: TimelineEvent[]): TimelineDay[] {
  const days: TimelineDay[] = [];
  for (const event of events) {
    const key = dayKey(event.at);
    let day = days[days.length - 1];
    if (day?.key !== key) {
      day = { key, date: event.at, entries: [] };
      days.push(day);
    }
    const last = day.entries[day.entries.length - 1];
    const previous =
      last?.type === 'group'
        ? last.events[last.events.length - 1]
        : last?.type === 'event'
          ? last.event
          : undefined;
    if (
      last &&
      previous &&
      sameEditor(previous, event) &&
      Math.abs(previous.at.getTime() - event.at.getTime()) <= COLLAPSE_WINDOW_MS
    ) {
      if (last.type === 'group') {
        last.events.push(event);
      } else if (previous.actor.kind === 'user') {
        day.entries[day.entries.length - 1] = {
          type: 'group',
          id: `group-${previous.id}`,
          actor: previous.actor,
          events: [previous, event],
        };
      }
      continue;
    }
    day.entries.push({ type: 'event', event });
  }
  return days;
}

export function filterEvents(events: TimelineEvent[], filter: TimelineFilter): TimelineEvent[] {
  return filter === 'all' ? events : events.filter((e) => e.category === filter);
}

/** «πριν από 5 λεπτά», «χθες»; dates older than a week fall back to the short date. */
export function relativeTime(
  at: Date,
  now: number,
  language: string,
  region: RegionFormat,
): string {
  const seconds = Math.round((at.getTime() - now) / 1000);
  const abs = Math.abs(seconds);
  const rtf = new Intl.RelativeTimeFormat(language === 'en' ? 'en-GB' : 'el-GR', {
    numeric: 'auto',
  });
  if (abs < 45) return rtf.format(0, 'second');
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), 'minute');
  if (abs < 86_400) return rtf.format(Math.round(seconds / 3600), 'hour');
  if (abs < 7 * 86_400) return rtf.format(Math.round(seconds / 86_400), 'day');
  return new Intl.DateTimeFormat(region, {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  }).format(at);
}

/** Absolute «05/10/2026 14:32» (Part 4 §8.2 date-time: no comma between date and time). */
export function absoluteTime(at: Date, region: RegionFormat): string {
  const date = new Intl.DateTimeFormat(region, {
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
  }).format(at);
  const time = new Intl.DateTimeFormat(region, {
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).format(at);
  return `${date} ${time}`;
}

/** «Σήμερα», «Χθες» or «Δευτέρα 5 Οκτωβρίου» (year added when not the current year). */
export function dayLabel(
  date: Date,
  now: number,
  language: string,
  labels: { today: string; yesterday: string },
): string {
  const today = new Date(now);
  const startOfToday = new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime();
  const startOfDay = new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
  const diffDays = Math.round((startOfToday - startOfDay) / 86_400_000);
  if (diffDays === 0) return labels.today;
  if (diffDays === 1) return labels.yesterday;
  return new Intl.DateTimeFormat(language === 'en' ? 'en-GB' : 'el-GR', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    ...(date.getFullYear() === today.getFullYear() ? {} : { year: 'numeric' }),
  }).format(date);
}

/** Deterministic avatar colour index 1–8 from an id. */
export function avatarIndex(id: string): number {
  let hash = 0;
  for (const ch of id) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
  return (hash % 8) + 1;
}

export function initialsOf(name: string): string {
  const parts = name.split(/\s+/u).filter(Boolean);
  const first = parts[0]?.[0] ?? '';
  const last = parts.length > 1 ? (parts[parts.length - 1]?.[0] ?? '') : '';
  return (first + last).toLocaleUpperCase('el-GR');
}
