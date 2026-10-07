/**
 * Recently opened records, kept in this browser only so staff can get back to a policy or billing account:
 * no module of the slice offers a policy or account search. Only ids and business numbers are stored, never
 * personal data (D-SLC-05). Storage can be unavailable (private mode), so every access is guarded.
 */
export interface RecentRecord {
  id: string;
  /** Business number shown to the user (policy number, invoice number…). */
  label: string;
}

const maxRecent = 8;

export function readRecent(kind: string): RecentRecord[] {
  try {
    const raw = localStorage.getItem(`coreins.recent.${kind}`);
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed)
      ? parsed.filter(
          (r): r is RecentRecord =>
            typeof r === 'object' &&
            r !== null &&
            typeof (r as RecentRecord).id === 'string' &&
            typeof (r as RecentRecord).label === 'string',
        )
      : [];
  } catch {
    return [];
  }
}

export function rememberRecent(kind: string, record: RecentRecord): void {
  try {
    const next = [record, ...readRecent(kind).filter((r) => r.id !== record.id)].slice(
      0,
      maxRecent,
    );
    localStorage.setItem(`coreins.recent.${kind}`, JSON.stringify(next));
  } catch {
    // Not persisted; the screen works without it.
  }
}
