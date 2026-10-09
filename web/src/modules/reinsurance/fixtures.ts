import type { ContractCreateRequest, ContractView } from './api';

/** Illustrative EUR per-risk treaty matching the generated RI API schema. */
export const treatyDraft: ContractCreateRequest = {
  legalEntity: 'GR-TEST',
  contractType: 'XOL_PER_RISK',
  contractYear: 2026,
  currency: 'EUR',
  period: { from: '2026-01-01', to: '2027-01-01' },
  scope: { productCodes: ['MOTOR-GR'], coverageCodes: ['MTPL'] },
  clause: {
    alaeIncluded: false,
    statutoryInterestIncluded: false,
    recoveriesInure: 'REALISED_ONLY',
  },
  layers: [
    {
      layerNo: 1,
      attachment: { amount: '250000.00', currency: 'EUR' },
      limit: { amount: '500000.00', currency: 'EUR' },
      aad: { amount: '0.00', currency: 'EUR' },
      aal: null,
    },
  ],
  participations: [
    { reinsurerPartyId: '11111111-1111-4111-8111-111111111111', signedLinePct: '100', lead: true },
  ],
  placedPct: '100',
};
export const treaty: ContractView = {
  ...treatyDraft,
  contractId: '22222222-2222-4222-8222-222222222222',
  contractNumber: 'RI000000001',
  stableTreatyId: 'MOTOR-XOL',
  status: 'DRAFT',
  versionNo: 1,
  recordVersion: 4,
  maker: 'USER:dev:maker',
  createdAt: '2026-10-09T09:00:00Z',
};
