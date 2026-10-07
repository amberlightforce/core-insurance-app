/** Value model of the @mention composer (D-FE-08). Mentions are tokens with offsets into `text`. */

export interface MentionPerson {
  id: string;
  name: string;
  initials?: string;
  /** Secondary line in the list, e.g. the role or team. */
  meta?: string;
}

export interface Mention {
  id: string;
  name: string;
  /** Offset of the «@». */
  start: number;
  /** Offset just after the name. */
  end: number;
}

export interface MentionValue {
  text: string;
  mentions: Mention[];
}

export const emptyMentionValue: MentionValue = { text: '', mentions: [] };

/** Accent- and case-insensitive key: NFD, strip marks, lower-case (el), final sigma folded. */
export function normalizeForSearch(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase('el').replace(/ς/gu, 'σ');
}

/** People whose name (any word, or the whole name) starts with the query; whole-name matches first. */
export function filterPeople(people: MentionPerson[], query: string, limit = 8): MentionPerson[] {
  const q = normalizeForSearch(query);
  if (q === '') return people.slice(0, limit);
  const starts: MentionPerson[] = [];
  const words: MentionPerson[] = [];
  for (const person of people) {
    const name = normalizeForSearch(person.name);
    if (name.startsWith(q)) starts.push(person);
    else if (name.split(/\s+/u).some((w) => w.startsWith(q)) || name.includes(q))
      words.push(person);
  }
  return [...starts, ...words].slice(0, limit);
}

/** The active «@query» before the caret, if any: `{ start }` is the offset of the «@». */
export function activeQuery(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret);
  const match = /(^|\s)@([^\s@]{0,30})$/u.exec(before);
  if (!match) return null;
  const query = match[2] ?? '';
  return { start: caret - query.length - 1, query };
}

/**
 * Re-bases mentions after a free-text edit from `previous` to `next`: mentions before the edit stay, mentions
 * after it shift, and a mention the edit touched is dropped (the token was broken).
 */
export function rebaseMentions(previous: MentionValue, next: string): Mention[] {
  const old = previous.text;
  let prefix = 0;
  const maxPrefix = Math.min(old.length, next.length);
  while (prefix < maxPrefix && old[prefix] === next[prefix]) prefix += 1;
  let suffix = 0;
  const maxSuffix = Math.min(old.length, next.length) - prefix;
  while (suffix < maxSuffix && old[old.length - 1 - suffix] === next[next.length - 1 - suffix]) {
    suffix += 1;
  }
  const editStart = prefix;
  const editEnd = old.length - suffix;
  const delta = next.length - old.length;
  const out: Mention[] = [];
  for (const m of previous.mentions) {
    if (m.end <= editStart) {
      out.push(m);
    } else if (m.start >= editEnd) {
      out.push({ ...m, start: m.start + delta, end: m.end + delta });
    }
  }
  return out.filter((m) => next.slice(m.start, m.end) === `@${m.name}`);
}

/** Replaces the «@query» at `start…caret` with the mention and a trailing space. */
export function insertMention(
  value: MentionValue,
  person: MentionPerson,
  start: number,
  caret: number,
): { value: MentionValue; caret: number } {
  const token = `@${person.name}`;
  const text = `${value.text.slice(0, start)}${token} ${value.text.slice(caret)}`;
  const delta = token.length + 1 - (caret - start);
  const mentions = value.mentions
    .filter((m) => m.end <= start || m.start >= caret)
    .map((m) => (m.start >= caret ? { ...m, start: m.start + delta, end: m.end + delta } : m));
  mentions.push({ id: person.id, name: person.name, start, end: start + token.length });
  mentions.sort((a, b) => a.start - b.start);
  return { value: { text, mentions }, caret: start + token.length + 1 };
}

/** Text and mention segments for display. */
export function segments(
  value: MentionValue,
): ({ type: 'text'; text: string } | { type: 'mention'; mention: Mention })[] {
  const out: ({ type: 'text'; text: string } | { type: 'mention'; mention: Mention })[] = [];
  let cursor = 0;
  for (const m of [...value.mentions].sort((a, b) => a.start - b.start)) {
    if (m.start < cursor || value.text.slice(m.start, m.end) !== `@${m.name}`) continue;
    if (m.start > cursor) out.push({ type: 'text', text: value.text.slice(cursor, m.start) });
    out.push({ type: 'mention', mention: m });
    cursor = m.end;
  }
  if (cursor < value.text.length) out.push({ type: 'text', text: value.text.slice(cursor) });
  return out;
}
