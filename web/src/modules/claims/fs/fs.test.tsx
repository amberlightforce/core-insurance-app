import { screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ClaimView } from '../../../api/types';
import { mockApi, renderScreen, type MockRoute } from '../../../test/mockApi';
import { ClaimViewPage } from '../ClaimViewPage';
import type { FsCaseView } from './api';

vi.setConfig({ testTimeout: 60_000 });
afterEach(() => {
  vi.unstubAllGlobals();
});

const claimId = 'c1a10000-1111-4222-8333-000000000001';
const fsCaseId = 'f5000000-1111-4222-8333-000000000001';
const eur = (amount: string) => ({ amount, currency: 'EUR' });

const claim: ClaimView = {
  summary: {
    claimId,
    claimNumber: 'CLM000000001',
    policyId: '0f0f0f0f-1111-4222-8333-444444444444',
    policyNumber: 'POL000000021',
    insuredPartyId: '01a11800-5123-72da-8f3c-f5bda39e1cb0',
    snapshotRef: 's',
    snapshotKnownAt: '2026-10-05T09:00:00Z',
    snapshotStatus: 'VERIFIED',
    policyInForceAtLoss: true,
    policyStatusAtLoss: 'IN_FORCE',
    productCode: 'MOTOR-T10',
    lineOfBusiness: 'MOTOR',
    lossAt: '2026-10-05T10:30:00Z',
    lossDate: '2026-10-05',
    noticeOn: '2026-10-06',
    lossCause: 'COLLISION',
    channel: 'STAFF',
    handlingSegment: 'STANDARD',
    status: 'OPEN',
    subStatus: 'NEW',
    coverageInQuestion: false,
    openDays: 2,
    recordVersion: 3,
    createdAt: '2026-10-06T08:00:00Z',
  },
  legalEntity: 'GR-TEST',
  jurisdiction: 'GR',
  lossLocation: 'Αθήνα',
  description: 'Δοκιμή',
  exposures: [],
  claimants: [],
  incidents: [],
};

function fsCase(over: Partial<FsCaseView> = {}): FsCaseView {
  return {
    fsCaseId,
    claimId,
    role: 'OWN_INSURER',
    state: 'ELIGIBLE',
    counterpartyCode: 'INS-B',
    eligibility: {
      result: 'ELIGIBLE',
      reasons: [],
      ruleId: 'GR-FS-STUB',
      ruleVersion: '1',
      legalStatus: 'UNVERIFIED',
      provisional: true,
      clearingValue: eur('2400.00'),
      clearingValueBasis: 'ACTUAL',
    },
    clearingValue: eur('2400.00'),
    createdAt: '2026-10-06T08:00:00Z',
    recordVersion: 4,
    ...over,
  };
}

function routes(extra: MockRoute[]): MockRoute[] {
  return [
    ...extra,
    { method: 'GET', path: `/api/clm/v1/claims/${claimId}`, respond: () => ({ body: { claim } }) },
    {
      method: 'GET',
      path: '/api/clm/v1/financials/get',
      respond: () => ({
        body: {
          balancesByLine: [],
          exposures: [],
          totals: {
            reserved: eur('0.00'),
            paid: eur('0.00'),
            openReserve: eur('0.00'),
            incurred: eur('0.00'),
            recoveries: eur('0.00'),
            netIncurred: eur('0.00'),
          },
        },
      }),
    },
    {
      method: 'GET',
      path: `/api/clm/v1/claims/${claimId}/payments`,
      respond: () => ({ body: { items: [], nextCursor: null } }),
    },
    {
      method: 'GET',
      path: '/api/ri/v1/recoveries/list-by-claim',
      respond: () => ({ body: { items: [], nextCursor: null } }),
    },
  ];
}

const slow = { timeout: 20_000 };
const view = () =>
  renderScreen(<ClaimViewPage />, { path: '/claims/:claimId', url: `/claims/${claimId}` });

describe('claim file friendly settlement', () => {
  it('shows the eligible case and opens the own settlement with the record version only', async () => {
    const api = mockApi(
      routes([
        {
          method: 'GET',
          path: '/api/clm/v1/friendly-settlements',
          respond: () => ({ body: { items: [fsCase()], nextCursor: null } }),
        },
        {
          method: 'POST',
          path: '/api/clm/v1/friendly-settlements/open-own-settlement',
          respond: () => ({
            body: { fsCase: fsCase(), exposureId: 'e1', recoveryId: 'r1' },
          }),
        },
      ]),
    );
    const { user } = view();
    expect(await screen.findByText('Εισπρακτέο από την αντίπαλη', {}, slow)).toBeInTheDocument();
    expect(screen.getAllByText('Επιλέξιμη ✓').length).toBeGreaterThan(0);
    await user.click(screen.getByRole('button', { name: 'Άνοιγμα ιδίου διακανονισμού' }));
    await vi.waitFor(() => {
      expect(
        api.callsTo('POST', '/api/clm/v1/friendly-settlements/open-own-settlement'),
      ).toHaveLength(1);
    }, slow);
    const call = api.callsTo('POST', '/api/clm/v1/friendly-settlements/open-own-settlement')[0];
    expect(call?.body).toEqual({ fsCaseId, expectedRecordVersion: 4 });
  });

  it('lists the not-eligible reasons and the at-fault payable', async () => {
    mockApi(
      routes([
        {
          method: 'GET',
          path: '/api/clm/v1/friendly-settlements',
          respond: () => ({
            body: {
              items: [
                fsCase({
                  state: 'NOT_ELIGIBLE',
                  eligibility: {
                    result: 'NOT_ELIGIBLE',
                    reasons: ['AMOUNT_ABOVE_LIMIT'],
                    ruleId: 'r',
                    ruleVersion: '1',
                    legalStatus: 'UNVERIFIED',
                    provisional: true,
                  },
                }),
                fsCase({
                  fsCaseId: 'f5000000-1111-4222-8333-000000000002',
                  role: 'AT_FAULT_INSURER',
                }),
              ],
              nextCursor: null,
            },
          }),
        },
      ]),
    );
    view();
    expect(await screen.findByText('Το ποσό υπερβαίνει το όριο', {}, slow)).toBeInTheDocument();
    expect(screen.getByText('Πληρωτέο στην αντίπαλη')).toBeInTheDocument();
  });
});
