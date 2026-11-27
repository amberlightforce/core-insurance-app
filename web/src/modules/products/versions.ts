import type { ProductListItem } from '../../api/types';

export type ProductVersion = ProductListItem['versions'][number];

/** The version open for new business on a date: LOCKED, not abstract, window containing the date (newest first). */
export function versionInForce(versions: readonly ProductVersion[], today: string) {
  return versions.find(
    (v) =>
      v.status === 'LOCKED' &&
      !v.isAbstract &&
      v.newBusinessFrom <= today &&
      (v.newBusinessTo === null || today < v.newBusinessTo),
  );
}

export const isoToday = () => new Date().toISOString().slice(0, 10);
