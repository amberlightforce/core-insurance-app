import type {
  PackActivationPreview,
  PackActivationView,
  PackGetResponse,
} from '../../../api/types';

export const packId = '018f8000-0000-7000-8000-000000000081';
export const activationId = '018f8000-0000-7000-8000-000000000082';
export const issuedHash = 'a'.repeat(64);
export const preview: PackActivationPreview = {
  fromVersion: '0.2.0',
  toVersion: '0.1.0',
  window: { from: '2026-10-08T09:00:00Z', to: '2026-10-09T09:00:00Z' },
  hashesIssued: [issuedHash, 'b'.repeat(64)],
  keyDiff: Array.from({ length: 8 }, (_, index) => ({
    key: `tax.treatment.rule.synthetic_${index + 1}`,
    change: 'CHANGED' as const,
  })),
};
// The MKT owner must serialize the same canonical ActorKey used by native RI/PFC approval owners.
export const pendingActivation: PackActivationView = {
  activationId,
  pack: 'GR',
  legalEntity: 'GR-TEST',
  kind: 'ROLLBACK',
  from: '0.2.0',
  to: '0.1.0',
  status: 'PENDING_APPROVAL',
  reason: 'Synthetic recovery validation reason',
  requestedBy: 'USER:releasemgr',
  decidedBy: null,
  approvalRequestId: null,
  activatedAt: null,
  resultingHash: null,
};
export const pack: PackGetResponse = {
  packId,
  pack: 'GR',
  scope: 'COUNTRY',
  versions: ['0.1.0', '0.2.0'].map((version) => ({
    version,
    status: 'PUBLISHED',
    contentDigest: issuedHash,
    publishedAt: '2026-10-07T09:00:00Z',
  })),
  activeVersions: [
    {
      legalEntity: 'GR-TEST',
      version: '0.1.0',
      activationId,
      activeSince: preview.window.to!,
      configurationHash: 'c'.repeat(64),
    },
  ],
  activationHistory: [
    {
      ...pendingActivation,
      status: 'ACTIVE',
      decidedBy: 'USER:designauth',
      activatedAt: preview.window.to,
      resultingHash: 'c'.repeat(64),
    },
    {
      ...pendingActivation,
      activationId: '018f8000-0000-7000-8000-000000000083',
      kind: 'ACTIVATE',
      from: '0.1.0',
      to: '0.2.0',
      status: 'SUPERSEDED',
      decidedBy: 'USER:designauth',
      activatedAt: preview.window.from,
      resultingHash: issuedHash,
    },
  ],
};
