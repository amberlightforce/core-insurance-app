import type {
  PackActivationStatus,
  PackActivationView,
  PackActiveVersionView,
  PackVersionView,
} from '../../../api/types';

/** One legal entity's relation to one pack version, derived from the activation history (states are never rewritten). */
export type EntityLifecycle =
  | { legalEntity: string; state: 'ACTIVE'; since: string; hash: string }
  | {
      legalEntity: string;
      state: 'ROLLED_BACK';
      /** When the version became active and when the rollback replaced it (the PackRolledBack window). */
      from: string | null;
      to: string | null;
      /** The configuration hash issued while the version was active (the hash a bound transaction stored). */
      hash: string | null;
      rollbackId: string;
    }
  | { legalEntity: string; state: 'SUPERSEDED' };

/** An activation that took effect: ACTIVE, or SUPERSEDED by a later one. */
const applied = (status: PackActivationStatus) => status === 'ACTIVE' || status === 'SUPERSEDED';

/**
 * The lifecycle of a version per legal entity.
 * - ACTIVE: it is the entity's active version.
 * - ROLLED_BACK: an applied ROLLBACK replaced it; the window runs from its own activation to the rollback.
 * - SUPERSEDED: a later activation replaced it (not a rollback).
 * Versions never activated for an entity have no entry (they are just Published).
 */
export function versionLifecycle(
  version: string,
  active: readonly PackActiveVersionView[],
  history: readonly PackActivationView[],
): EntityLifecycle[] {
  const out: EntityLifecycle[] = [];
  const entities = new Set([
    ...active.map((a) => a.legalEntity),
    ...history.map((h) => h.legalEntity),
  ]);
  for (const legalEntity of entities) {
    const current = active.find((a) => a.legalEntity === legalEntity);
    if (current?.version === version) {
      out.push({
        legalEntity,
        state: 'ACTIVE',
        since: current.activeSince,
        hash: current.configurationHash,
      });
      continue;
    }
    const mine = history.filter((h) => h.legalEntity === legalEntity && applied(h.status));
    const rollback = mine
      .filter((h) => h.kind === 'ROLLBACK' && h.from === version)
      .sort((a, b) => (b.activatedAt ?? '').localeCompare(a.activatedAt ?? ''))[0];
    // The latest activation of this version that took effect before the rollback.
    const activation = mine
      .filter(
        (h) =>
          h.to === version &&
          h.activatedAt !== null &&
          (!rollback?.activatedAt || h.activatedAt <= rollback.activatedAt),
      )
      .sort((a, b) => (b.activatedAt ?? '').localeCompare(a.activatedAt ?? ''))[0];
    if (rollback) {
      out.push({
        legalEntity,
        state: 'ROLLED_BACK',
        from: activation?.activatedAt ?? null,
        to: rollback.activatedAt,
        hash: activation?.resultingHash ?? null,
        rollbackId: rollback.activationId,
      });
    } else if (activation) {
      out.push({ legalEntity, state: 'SUPERSEDED' });
    }
  }
  return out;
}

/** Published versions an entity can move to: everything Published except what is already active there. */
export function targetVersions(
  versions: readonly PackVersionView[],
  active: PackActiveVersionView | undefined,
): PackVersionView[] {
  return versions.filter((v) => v.status === 'PUBLISHED' && v.version !== active?.version);
}

/** True when the activation is waiting for a checker. */
export const isPending = (a: Pick<PackActivationView, 'status'>) =>
  a.status === 'PENDING_APPROVAL' || a.status === 'REQUESTED';

/** The minimum length of a request reason (contract: 20..128). */
export const reasonMin = 20;
export const reasonMax = 128;
