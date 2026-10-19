import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ClaimView } from '../../../api/types';
import { mockApi, problem, renderScreen, type MockRoute } from '../../../test/mockApi';
import { ClaimViewPage } from '../ClaimViewPage';
import { closeGuardFindings } from '../closeGuard';
import { ApiError } from '../../../api/client';
import type { RecoveryView } from './api';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const claimId = 'c1a10000-1111-4222-8333-000000000001';
const policyId = '0f0f0f0f-1111-4222-8333-444444444444';
const insuredId = '01a11800-5123-72da-8f3c-f5bda39e1cb0';
const exposureId = 'e0000000-1111-4222-8333-000000000001';
const recoveryId = 'd0000000-1111-4222-8333-000000000001';
const insurerId = '01a11800-5123-72da-8f3c-f5bda39e1cb9';

const claim: ClaimView = {
  summary: {
    claimId,
    claimNumber: 'CLM000000001',
    policyId,
    policyNumber: 'POL000000021',
    insuredPartyId: insuredId,
    snapshotRef: 'snap-1',
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
  exposures: [
    {
      exposureId,
      exposureNumber: 'CLM000000001-01',
      kind: 'OWN_DAMAGE',
      coverageCode: 'OWN-DAMAGE',
      claimantId: 'c0000000-1111-4222-8333-000000000001',
      claimantPartyId: insuredId,
      status: 'OPEN',
      coverageIndication: 'COVERED',
      coverageDecision: 'PENDING',
      createdAt: '2026-10-06T08:00:00Z',
    },
  ],
  claimants: [],
  incidents: [],
  liabilityFacts: { insuredFaultPct: '30.00' },
};

const eur = (amount: string) => ({ amount, currency: 'EUR' });

function recovery(over: Partial<RecoveryView> = {}): RecoveryView {
  return {
    recoveryId,
    claimId,
    type: 'SUBROGATION',
    state: 'OPEN',
    counterpartyPartyId: insurerId,
    expectedAmount: eur('3000.00'),
    recoveryReserve: eur('3000.00'),
    recoveredAmount: eur('0.00'),
    allocationRule: 'PRO_RATA_PAID',
    milestones: [],
    recordVersion: 2,
    ...over,
  } as RecoveryView;
}

const balance = (extra: object = {}) => ({
  reserved: eur('5000.00'),
  paid: eur('5000.00'),
  openReserve: eur('0.00'),
  incurred: eur('5000.00'),
  ...extra,
});

function routes(extra: MockRoute[] = []): MockRoute[] {
  // The first matching route wins, so the test's own routes go before the defaults.
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
          totals: balance({ recoveries: eur('0.00'), netIncurred: eur('2000.00') }),
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
      path: `/api/clm/v1/claims/${claimId}/payee-accounts`,
      respond: () => ({ body: { items: [], nextCursor: null } }),
    },
    {
      method: 'GET',
      path: '/api/clm/v1/transaction-sets',
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

const view = (tab?: string) =>
  renderScreen(<ClaimViewPage />, {
    path: '/claims/:claimId',
    url: `/claims/${claimId}${tab ? `?tab=${tab}` : ''}`,
  });

describe('claim file recoveries', () => {
  it('lists the cases, suggests no subrogation when one exists and demands with a payment reference', async () => {
    const api = mockApi(
      routes([
        {
          method: 'GET',
          path: '/api/clm/v1/recoveries',
          respond: () => ({ body: { items: [recovery()], nextCursor: null } }),
        },
        {
          method: 'POST',
          path: '/api/clm/v1/recoveries/demand',
          respond: () => ({
            status: 201,
            body: {
              recovery: recovery({ state: 'DEMANDED' }),
              receivableId: 'r0000000-1111-4222-8333-000000000001',
              billingAccountId: 'b0000000-1111-4222-8333-000000000001',
              paymentReference: 'RF12345678',
            },
          }),
        },
      ]),
    );
    const { user } = view('recoveries');
    expect(await screen.findByText('Υποκατάσταση', {}, slow)).toBeInTheDocument();
    expect(screen.queryByText('Προτείνεται υποκατάσταση')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Απαίτηση' }));
    const form = await screen.findByRole('form', { name: 'Απαίτηση ποσού' }, slow);
    const [day] = within(
      within(form).getByRole('group', { name: /Ημερομηνία πληρωμής/ }),
    ).getAllByRole('spinbutton');
    if (!day) throw new Error('no date field');
    await user.click(day);
    await user.keyboard('31122026');
    await user.click(within(form).getByRole('button', { name: 'Δημιουργία απαίτησης' }));
    expect(await screen.findByText('RF12345678', {}, slow)).toBeInTheDocument();
    const call = api.callsTo('POST', '/api/clm/v1/recoveries/demand')[0];
    expect(call?.body).toMatchObject({ recoveryId, expectedRecordVersion: 2 });
    expect(call?.headers.get('Idempotency-Key')).toBeTruthy();
  });

  it('proposes a subrogation from the fault share when none exists', async () => {
    mockApi(
      routes([
        {
          method: 'GET',
          path: '/api/clm/v1/recoveries',
          respond: () => ({ body: { items: [], nextCursor: null } }),
        },
      ]),
    );
    view('recoveries');
    expect(await screen.findByText('Προτείνεται υποκατάσταση', {}, slow)).toBeInTheDocument();
  });

  it('shows recovery lines and net incurred in the money card', async () => {
    mockApi(
      routes([
        {
          method: 'GET',
          path: '/api/clm/v1/recoveries',
          respond: () => ({
            body: {
              items: [
                recovery({
                  type: 'SALVAGE',
                  recoveredAmount: eur('1000.00'),
                  recoveryReserve: eur('500.00'),
                }),
              ],
              nextCursor: null,
            },
          }),
        },
      ]),
    );
    view();
    expect(await screen.findByText('Ανάκτηση · Διάσωση', {}, slow)).toBeInTheDocument();
    expect(screen.getByText('Ανάκτηση · Διάσωση (αναμενόμενη)')).toBeInTheDocument();
    expect(screen.getByText('Καθαρό κόστος ζημίας')).toBeInTheDocument();
  });

  it('explains a payment that can no longer be stopped', async () => {
    mockApi(
      routes([
        {
          method: 'GET',
          path: `/api/clm/v1/claims/${claimId}/payments`,
          respond: () => ({
            body: {
              items: [
                {
                  claimPaymentId: 'p0000000-1111-4222-8333-000000000001',
                  setId: 's0000000-1111-4222-8333-000000000001',
                  exposureId,
                  payeePartyId: insuredId,
                  payeeAccountId: 'a0000000-1111-4222-8333-000000000001',
                  amount: eur('5000.00'),
                  status: 'ISSUED',
                  fiscalMark: '400123456789012',
                },
              ],
              nextCursor: null,
            },
          }),
        },
        {
          method: 'POST',
          path: '/api/clm/v1/payments/stop',
          respond: () => problem(409, 'CLM-ERR-NOT-STOPPABLE', 'Not stoppable'),
        },
      ]),
    );
    const { user } = view('financials');
    await user.click(await screen.findByRole('button', { name: 'Διακοπή' }, slow));
    const form = await screen.findByRole('form', { name: 'Διακοπή πληρωμής' }, slow);
    await user.click(within(form).getByRole('button', { name: /Αιτιολογία/ }));
    await user.click(await screen.findByRole('option', { name: 'Λάθος ποσό' }, slow));
    await user.click(within(form).getByRole('button', { name: 'Διακοπή πληρωμής' }));
    await waitFor(() => {
      expect(screen.getByText('Η πληρωμή δεν μπορεί πια να σταματήσει')).toBeInTheDocument();
    }, slow);
    expect(screen.getByText('400123456789012')).toBeInTheDocument();
  });
});

describe('closeGuardFindings', () => {
  it('reads errors[] items with exposure ids and claim-level guards', () => {
    const error = new ApiError({
      status: 422,
      code: 'CLM-ERR-CLOSE-GUARD',
      errors: [
        { code: 'OPEN_RESERVE', exposureIds: [exposureId] },
        { code: 'LITIGATION_OPEN', exposureIds: [] },
      ] as never,
    });
    expect(closeGuardFindings(error)).toEqual([
      { exposure: exposureId, reasons: ['OPEN_RESERVE'] },
      { exposure: '', reasons: ['LITIGATION_OPEN'] },
    ]);
  });
});
