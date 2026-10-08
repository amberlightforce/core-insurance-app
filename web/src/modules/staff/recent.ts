/**
 * Recently opened records, kept in this browser only so staff can get back to a policy, claim, invoice or
 * billing account: no module of the slice offers a search for them. Only the record kind and its opaque
 * internal id are stored, never a business number, name or any other personal data (D-SLC-05). The number
 * shown to the user is resolved from the API at read time (`useRecentLabels`). Storage can be unavailable
 * (private mode), so every access is guarded. Entries written by earlier versions (id + number) live under
 * the old key, which is ignored and removed.
 */
const maxRecent = 8;
const keyOf = (kind: string) => `coreins.recent.v2.${kind}`;
const legacyKeyOf = (kind: string) => `coreins.recent.${kind}`;

export function readRecent(kind: string): string[] {
  try {
    const raw = localStorage.getItem(keyOf(kind));
    const parsed: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed)
      ? parsed.filter((id): id is string => typeof id === 'string').slice(0, maxRecent)
      : [];
  } catch {
    return [];
  }
}

export function rememberRecent(kind: string, id: string): void {
  try {
    const next = [id, ...readRecent(kind).filter((x) => x !== id)].slice(0, maxRecent);
    localStorage.setItem(keyOf(kind), JSON.stringify(next));
    localStorage.removeItem(legacyKeyOf(kind));
  } catch {
    // Not persisted; the screen works without it.
  }
}
