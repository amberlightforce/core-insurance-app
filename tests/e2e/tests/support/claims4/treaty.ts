import { randomUUID } from 'node:crypto';
import { expect, type APIRequestContext } from '@playwright/test';
import { call, signIn, type Json } from '../api.js';
import { tokenFor } from './signin.js';

export interface XolOptions {
  contractYear?: number;
  attachment?: string;
  limit?: string;
  productCodes?: string[];
  coverageCodes?: string[];
  /** Reinsurer party; a synthetic organisation is created when omitted. */
  reinsurerPartyId?: string;
}

const money = (amount: string) => ({ amount, currency: 'EUR' });

/**
 * `riacct` creates and submits a per-risk XoL treaty, `rimgr` (a different person; SoD) approves it. Returns the contract id.
 * Defaults to a far-future contract year so it never collides with other journeys' treaties.
 */
export async function createAndApproveXolTreaty(request: APIRequestContext, options: XolOptions = {}): Promise<string> {
  const year = options.contractYear ?? 2035;
  const accountant = await tokenFor(request, 'riacct');
  const manager = await tokenFor(request, 'rimgr');
  let reinsurer = options.reinsurerPartyId;
  if (!reinsurer) {
    const underwriter = await signIn(request, 'underwriter');
    const name = `Synthetic Reinsurer ${randomUUID().slice(0, 8)}`;
    const party = await call(request, underwriter, 'POST', '/api/pty/v1/parties', { partyType: 'ORGANISATION', organisation: { legalName: name } });
    expect(party.status, party.text).toBe(201);
    reinsurer = party.body['party']['partyId'] as string;
  }
  const created = await call(request, accountant, 'POST', '/api/ri/v1/contracts', {
    legalEntity: 'GR-TEST', contractType: 'XOL_PER_RISK', contractYear: year, currency: 'EUR',
    period: { from: `${year}-01-01`, to: `${year + 1}-01-01` },
    scope: { productCodes: options.productCodes ?? ['MOTOR-GR'], coverageCodes: options.coverageCodes ?? ['OWN-DAMAGE'] },
    clause: { alaeIncluded: true, statutoryInterestIncluded: false, recoveriesInure: 'REALISED_ONLY' },
    layers: [{ layerNo: 1, attachment: money(options.attachment ?? '500000.00'), limit: money(options.limit ?? '500000.00'), aad: money('0.00') }],
    participations: [{ reinsurerPartyId: reinsurer, signedLinePct: '100', lead: true }], placedPct: '100',
  });
  expect(created.status, `create treaty: ${created.text}`).toBe(201);
  const contract = created.body['contract'] as Json;
  const submitted = await call(request, accountant, 'POST', '/api/ri/v1/contracts/submit', {
    contractId: contract['contractId'], expectedRecordVersion: contract['recordVersion'],
  });
  expect(submitted.status, `submit treaty: ${submitted.text}`).toBe(200);
  const approved = await call(request, manager, 'POST', '/api/ri/v1/contracts/approve', {
    contractId: contract['contractId'], expectedRecordVersion: submitted.body['contract']['recordVersion'], decision: 'APPROVE',
  });
  expect(approved.status, `approve treaty: ${approved.text}`).toBe(200);
  return contract['contractId'] as string;
}
