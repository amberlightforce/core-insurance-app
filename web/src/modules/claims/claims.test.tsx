import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type {
  ApprovalView,
  ClaimSummary,
  ClaimView,
  FnolSubmitResponse,
  PolicySearchItem,
} from '../../api/types';
import { expectNoA11yViolations } from '../../test/axe';
import { mockApi, problem, renderScreen, type MockRoute } from '../../test/mockApi';
import { ApprovalDetailPage } from './ApprovalDetailPage';
import { ApprovalsInboxPage } from './ApprovalsInboxPage';
import { ClaimsHomePage } from './ClaimsHomePage';
import { ClaimViewPage } from './ClaimViewPage';
import { FnolPage } from './FnolPage';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

type User = ReturnType<typeof renderScreen>['user'];

const claimId = 'c1a10000-1111-4222-8333-000000000001';
const policyId = '0f0f0f0f-1111-4222-8333-444444444444';
const insuredId = '01a11800-5123-72da-8f3c-f5bda39e1cb0';
const requestId = 'a9900000-1111-4222-8333-000000000009';
const hash = '50020ebe2604343834cd2490641a2ed0952417ecb03c691b0b0488dc2a04c50c';
const iban = 'GR1601101250000000012300695';

const policy: PolicySearchItem = {
  policyId,
  policyNumber: 'POL000000021',
  productCode: 'MOTOR-T10',
  insuredPartyId: insuredId,
  status: 'IN_FORCE',
  termPeriod: { from: '2026-01-01T00:00:00Z', to: '2027-01-01T00:00:00Z' },
};

function summary(over: Partial<ClaimSummary> = {}): ClaimSummary {
  return {
    claimId,
    claimNumber: 'CLM000000001',
    policyId,
    policyNumber: 'POL000000021',
    insuredPartyId: insuredId,
    snapshotRef: 'snap-7f3a91',
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
    ...over,
  };
}

const exposure1 = {
  exposureId: 'e0000000-1111-4222-8333-000000000001',
  exposureNumber: 'CLM000000001-01',
  kind: 'OWN_DAMAGE' as const,
  coverageCode: 'OD',
  claimantId: 'c0000000-1111-4222-8333-000000000001',
  claimantPartyId: insuredId,
  status: 'OPEN' as const,
  coverageIndication: 'COVERED' as const,
  coverageDecision: 'PENDING' as const,
  createdAt: '2026-10-06T08:00:00Z',
};

function claim(over: Partial<ClaimSummary> = {}, exposures = [exposure1]): ClaimView {
  return {
    summary: summary(over),
    legalEntity: 'GR-TEST',
    jurisdiction: 'GR',
    lossLocation: 'Λεωφ. Συγγρού 120, Αθήνα',
    description: 'Πρόσκρουση στο πίσω μέρος (δοκιμαστικό).',
    exposures,
    claimants: [{ claimantId: exposure1.claimantId, partyId: insuredId, claimantType: 'INSURED' }],
    incidents: [],
  };
}

function approval(over: Partial<ApprovalView> = {}): ApprovalView {
  return {
    requestId,
    type: 'CLM.PAYMENT',
    objectRef: {
      module: 'CLM',
      type: 'TransactionSet',
      id: 'f0000000-1111-4222-8333-000000000001',
    },
    payloadHash: hash,
    status: 'PendingApproval',
    maker: { kind: 'USER', id: 'claims' },
    authority: { type: 'CLM.PAYMENT', amount: { amount: '5500.00', currency: 'EUR' } },
    referralRole: 'Staff.ClaimsManager',
    reason: 'Πλήρης αποζημίωση επισκευής',
    diff: {
      amount: {
        from: { amount: '0.00', currency: 'EUR' },
        to: { amount: '5500.00', currency: 'EUR' },
      },
      costType: 'VEHICLE_REPAIR',
    },
    requestedAt: '2026-10-07T09:15:00Z',
    version: 1,
    ...over,
  };
}

const withErrors = (status: number, code: string, title: string, extra: object) => {
  const base = problem(status, code, title);
  return { status, body: { ...base.body, ...extra } };
};

/* ------------------------------------------------------------------ claim search */

describe('ClaimsHomePage', () => {
  const view = () => renderScreen(<ClaimsHomePage />, { path: '/claims', url: '/claims' });

  it('searches by policy number in a POST body, lists the claims and has no a11y violations', async () => {
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/clm/v1/claims/search',
        respond: () => ({ body: { items: [{ claim: summary() }], nextCursor: null } }),
      },
    ]);
    const { user, container } = view();
    expect(await screen.findByRole('heading', { level: 1, name: 'Ζημίες' })).toBeInTheDocument();
    expect(screen.getByText('Δεν υπάρχουν πρόσφατες ζημίες')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Αναζήτηση' }));
    expect(
      await screen.findAllByText('Δώστε αριθμό ζημίας ή αριθμό ασφαλιστηρίου.'),
    ).not.toHaveLength(0);
    expect(api.calls).toHaveLength(0);

    await user.type(screen.getByRole('textbox', { name: 'Αριθμός ασφαλιστηρίου' }), 'POL000000021');
    await user.click(screen.getByRole('button', { name: 'Αναζήτηση' }));
    const grid = await screen.findByRole('grid', { name: 'Αποτελέσματα αναζήτησης ζημιών' });
    expect(within(grid).getByText('CLM000000001')).toBeInTheDocument();
    const [call] = api.callsTo('POST', '/api/clm/v1/claims/search');
    expect(call?.body).toEqual({ policyNumber: 'POL000000021' });
    expect(call?.url.search).not.toContain('POL000000021');
    await expectNoA11yViolations(container);
  });

  it('shows an empty result and a permission error', async () => {
    let denied = false;
    mockApi([
      {
        method: 'POST',
        path: '/api/clm/v1/claims/search',
        respond: () =>
          denied
            ? problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden')
            : { body: { items: [], nextCursor: null } },
      },
    ]);
    const { user } = view();
    await user.type(screen.getByRole('textbox', { name: 'Αριθμός ζημίας' }), 'CLM404');
    await user.click(screen.getByRole('button', { name: 'Αναζήτηση' }));
    expect(await screen.findByText('Δεν βρέθηκαν ζημίες')).toBeInTheDocument();
    denied = true;
    await user.click(screen.getByRole('button', { name: 'Αναζήτηση' }));
    expect(await screen.findByText('Η αναζήτηση απέτυχε')).toBeInTheDocument();
  });
});

/* ------------------------------------------------------------------------- FNOL */

const submitted: FnolSubmitResponse = {
  claimId,
  claimNumber: 'CLM000000001',
  claim: summary(),
  exposures: [exposure1],
  coverageIndications: [{ coverageCode: 'OD', indication: 'COVERED' }],
  duplicateCandidates: [],
  handlingSegment: 'STANDARD',
};

const valid = {
  valid: true,
  issues: [],
  policyNumber: 'POL000000021',
  policyInForce: true,
  coverageIndications: [],
  duplicateCandidates: [],
};

function fnolRoutes(over: { validate?: MockRoute['respond']; submit?: MockRoute['respond'] } = {}) {
  return [
    {
      method: 'POST',
      path: '/api/pol/v1/policies/search',
      respond: () => ({ body: { items: [policy], nextCursor: null } }),
    },
    {
      method: 'POST',
      path: '/api/clm/v1/fnol/validate',
      respond: over.validate ?? (() => ({ body: valid })),
    },
    {
      method: 'POST',
      path: '/api/clm/v1/fnol/submit',
      respond: over.submit ?? (() => ({ body: submitted })),
    },
  ] satisfies MockRoute[];
}

async function fillFnol(user: User) {
  await user.type(screen.getByRole('textbox', { name: /Αριθμός ασφαλιστηρίου/ }), 'POL000000021');
  await user.click(screen.getByRole('button', { name: 'Αναζήτηση' }));
  await screen.findByLabelText('Σύνοψη ασφαλιστηρίου');
  await user.type(screen.getByRole('textbox', { name: /Ώρα ζημίας/ }), '00:30');
  await user.click(screen.getByRole('button', { name: /Αιτία ζημίας/ }));
  await user.click(await screen.findByRole('option', { name: 'Σύγκρουση' }));
  await user.type(screen.getByRole('textbox', { name: /Τόπος ζημίας/ }), 'Λεωφ. Συγγρού 120');
  await user.type(screen.getByRole('textbox', { name: /Περιγραφή/ }), 'Πρόσκρουση στο πίσω μέρος');
}

describe('FnolPage', () => {
  const view = () => renderScreen(<FnolPage />, { path: '/claims/new', url: '/claims/new' });

  it('finds the policy by number, shows its summary and has no a11y violations', async () => {
    const api = mockApi(fnolRoutes());
    const { user, container } = view();
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Αναγγελία ζημίας' }),
    ).toBeInTheDocument();
    await user.type(screen.getByRole('textbox', { name: /Αριθμός ασφαλιστηρίου/ }), 'POL000000021');
    await user.click(screen.getByRole('button', { name: 'Αναζήτηση' }));
    const summaryList = await screen.findByLabelText('Σύνοψη ασφαλιστηρίου');
    expect(within(summaryList).getByText('MOTOR-T10')).toBeInTheDocument();
    expect(within(summaryList).getByText('01/01/2026 – 01/01/2027')).toBeInTheDocument();
    const [call] = api.callsTo('POST', '/api/pol/v1/policies/search');
    expect(call?.body).toEqual({ policyNumber: 'POL000000021' });
    expect(call?.url.search).not.toContain('POL000000021');
    await expectNoA11yViolations(container);
  });

  it('says so when no policy matches', async () => {
    mockApi([
      {
        method: 'POST',
        path: '/api/pol/v1/policies/search',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    const { user } = view();
    await user.type(screen.getByRole('textbox', { name: /Αριθμός ασφαλιστηρίου/ }), 'NOPE');
    await user.click(screen.getByRole('button', { name: 'Αναζήτηση' }));
    expect(await screen.findByText('Δεν βρέθηκε ασφαλιστήριο')).toBeInTheDocument();
  });

  it('does not call the API while required fields are missing', async () => {
    const api = mockApi(fnolRoutes());
    const { user } = view();
    await user.click(screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' }));
    expect(await screen.findAllByText('Βρείτε και επιλέξτε το ασφαλιστήριο.')).not.toHaveLength(0);
    expect(screen.getAllByText('Επιλέξτε αιτία ζημίας.').length).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/clm/v1/fnol/validate')).toHaveLength(0);
  });

  it('validates, then submits with an Idempotency-Key that is reused on retry', async () => {
    let attempt = 0;
    const api = mockApi(
      fnolRoutes({
        submit: () =>
          ++attempt === 1
            ? problem(503, 'CLM-ERR-DEPENDENCY-UNAVAILABLE', 'Μη διαθέσιμη υπηρεσία')
            : { body: submitted },
      }),
    );
    const { user } = view();
    await fillFnol(user);
    const submit = screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' });
    await user.click(submit);
    expect(await screen.findByText('Η αναγγελία δεν καταχωρίστηκε')).toBeInTheDocument();
    await user.click(submit);
    expect(await screen.findByRole('button', { name: 'Άνοιγμα ζημίας' })).toBeInTheDocument();

    expect(api.callsTo('POST', '/api/clm/v1/fnol/validate').length).toBeGreaterThanOrEqual(2);
    const [first, second] = api.callsTo('POST', '/api/clm/v1/fnol/submit');
    expect(first?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(second?.headers.get('Idempotency-Key')).toBe(first?.headers.get('Idempotency-Key'));
    expect(second?.body).toMatchObject({
      lineOfBusiness: 'MOTOR',
      policyId,
      policyNumber: 'POL000000021',
      lossCause: 'COLLISION',
      channel: 'STAFF',
      receiptMedium: 'TELEPHONE',
      reporter: { partyId: insuredId, relationship: 'INSURED' },
      exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OD' }],
    });
    const body = second?.body as { lossAt: string; noticeOn: string };
    expect(body.lossAt).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:00Z$/);
    expect(body.noticeOn).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    // Free text never travels in a URL.
    for (const call of api.calls) expect(call.url.search).not.toContain('Συγγρού');
    expect(screen.getByRole('button', { name: 'Άνοιγμα ζημίας' })).toBeInTheDocument();
  });

  it('asks for a LINK / OVERRIDE decision with a reason before submitting a probable duplicate', async () => {
    const api = mockApi(
      fnolRoutes({
        validate: () => ({
          body: {
            ...valid,
            duplicateCandidates: [
              { claimId, claimNumber: 'CLM000000007', reasons: ['SAME_POLICY', 'SAME_LOSS_DATE'] },
            ],
          },
        }),
      }),
    );
    const { user } = view();
    await fillFnol(user);
    await user.click(screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' }));
    expect(await screen.findByText('Βρέθηκαν ζημίες που μοιάζουν με αυτή')).toBeInTheDocument();
    expect(screen.getByText(/Ίδιο ασφαλιστήριο · Ίδια ημερομηνία ζημίας/)).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/clm/v1/fnol/submit')).toHaveLength(0);

    // Without a reason nothing is sent, and the error says why.
    await user.click(screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' }));
    expect(await screen.findAllByText(/δώστε αιτιολογία|δώστε αιτιολογία\./i)).not.toHaveLength(0);
    expect(api.callsTo('POST', '/api/clm/v1/fnol/submit')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: /Αιτιολογία/ }));
    await user.click(await screen.findByRole('option', { name: 'Διαφορετική ζημία' }));
    await user.click(screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' }));
    await screen.findByRole('button', { name: 'Άνοιγμα ζημίας' });
    const [call] = api.callsTo('POST', '/api/clm/v1/fnol/submit');
    expect(call?.body).toMatchObject({
      duplicateDecision: { action: 'OVERRIDE', reasonCode: 'DIFFERENT_LOSS' },
    });
  });

  it('links to the existing claim when LINK is chosen', async () => {
    const api = mockApi(
      fnolRoutes({
        validate: () => ({
          body: {
            ...valid,
            duplicateCandidates: [
              { claimId, claimNumber: 'CLM000000007', reasons: ['SAME_POLICY'] },
            ],
          },
        }),
      }),
    );
    const { user } = view();
    await fillFnol(user);
    await user.click(screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' }));
    await screen.findByText('Βρέθηκαν ζημίες που μοιάζουν με αυτή');
    await user.click(screen.getByRole('radio', { name: 'Σύνδεση με την υπάρχουσα ζημία' }));
    await user.click(screen.getByRole('button', { name: /Αιτιολογία/ }));
    await user.click(await screen.findByRole('option', { name: 'Ίδια ζημία' }));
    await user.click(screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' }));
    await screen.findByRole('button', { name: 'Άνοιγμα ζημίας' });
    const [call] = api.callsTo('POST', '/api/clm/v1/fnol/submit');
    expect(call?.body).toMatchObject({
      duplicateDecision: { action: 'LINK', linkedClaimId: claimId, reasonCode: 'SAME_LOSS' },
    });
  });

  it('shows 422 errors on the field they belong to and does not submit', async () => {
    const api = mockApi(
      fnolRoutes({
        validate: () =>
          withErrors(422, 'CLM-ERR-LOSS-DATE', 'Μη αποδεκτή ημερομηνία', {
            errors: [
              {
                field: 'noticeOn',
                code: 'LOSS-DATE',
                message: 'Η αναγγελία είναι μελλοντική (διακομιστής).',
              },
            ],
          }),
      }),
    );
    const { user } = view();
    await fillFnol(user);
    await user.click(screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' }));
    expect(
      (await screen.findAllByText('Η αναγγελία είναι μελλοντική (διακομιστής).')).length,
    ).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/clm/v1/fnol/submit')).toHaveLength(0);
  });

  it('warns that coverage is in question when the policy was not in force at the loss', async () => {
    mockApi(
      fnolRoutes({
        validate: () => ({
          body: { ...valid, policyInForce: false, policyStatusAtLoss: 'EXPIRED' },
        }),
      }),
    );
    const { user } = view();
    await fillFnol(user);
    await user.click(screen.getByRole('button', { name: 'Έλεγχος' }));
    expect(await screen.findByText('Η κάλυψη τίθεται υπό αμφισβήτηση')).toBeInTheDocument();
    expect(screen.getByText(/θα ανοίξει με την κάλυψη υπό αμφισβήτηση/)).toBeInTheDocument();
  });

  it('shows a permission message when the role may not submit', async () => {
    mockApi(fnolRoutes({ submit: () => problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden') }));
    const { user } = view();
    await fillFnol(user);
    await user.click(screen.getByRole('button', { name: 'Καταχώριση αναγγελίας' }));
    expect(await screen.findByText('Η αναγγελία δεν καταχωρίστηκε')).toBeInTheDocument();
    expect(screen.getByText(/δεν επιτρέπει αυτή την ενέργεια/)).toBeInTheDocument();
  });
});

/* ------------------------------------------------------------------- claim view */

function viewRoutes(state: { claim: ClaimView }, extra: MockRoute[] = []): MockRoute[] {
  return [
    {
      method: 'GET',
      path: `/api/clm/v1/claims/${claimId}`,
      respond: () => ({ body: { claim: state.claim } }),
    },
    ...extra,
  ];
}

const claimView = () =>
  renderScreen(<ClaimViewPage />, { path: '/claims/:claimId', url: `/claims/${claimId}` });

describe('ClaimViewPage', () => {
  it('shows the header, coverage indication, snapshot ref and exposures', async () => {
    mockApi(viewRoutes({ claim: claim() }));
    const { container } = claimView();
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Ζημία CLM000000001' }),
    ).toBeInTheDocument();
    const header = screen.getByRole('region', { name: 'Στοιχεία ζημίας' });
    expect(within(header).getByText('POL000000021')).toBeInTheDocument();
    expect(within(header).getByText('Σύγκρουση')).toBeInTheDocument();
    expect(within(header).getByText(/^05\/10\/2026/)).toBeInTheDocument();
    const coverage = screen.getByRole('region', { name: 'Κάλυψη' });
    expect(within(coverage).getByText('snap-7f3a91')).toBeInTheDocument();
    expect(
      within(coverage).getByText('Σύμφωνα με το στιγμιότυπο ασφαλιστηρίου'),
    ).toBeInTheDocument();
    const grid = screen.getByRole('grid', { name: 'Εκθέσεις' });
    expect(within(grid).getByText('CLM000000001-01')).toBeInTheDocument();
    expect(within(grid).getByText('Ίδιες ζημιές')).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('warns when coverage is in question', async () => {
    mockApi(
      viewRoutes({
        claim: claim({ coverageInQuestion: true, policyInForceAtLoss: false }),
      }),
    );
    claimView();
    expect(await screen.findByText('Η κάλυψη τίθεται υπό αμφισβήτηση')).toBeInTheDocument();
    expect(screen.getAllByText('Υπό αμφισβήτηση').length).toBeGreaterThan(0);
  });

  it('creates an own-damage exposure for the insured with the record version and an Idempotency-Key', async () => {
    const state = { claim: claim({}, []) };
    const api = mockApi(
      viewRoutes(state, [
        {
          method: 'POST',
          path: '/api/clm/v1/exposures',
          respond: () => {
            state.claim = claim({ recordVersion: 4 });
            return { status: 201, body: { exposure: exposure1, claim: state.claim.summary } };
          },
        },
      ]),
    );
    const { user } = claimView();
    expect(await screen.findByText('Δεν υπάρχουν εκθέσεις')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Δημιουργία έκθεσης' }));
    const grid = await screen.findByRole('grid', { name: 'Εκθέσεις' });
    expect(await within(grid).findByText('CLM000000001-01')).toBeInTheDocument();
    const [call] = api.callsTo('POST', '/api/clm/v1/exposures');
    expect(call?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(call?.body).toEqual({
      claimId,
      expectedRecordVersion: 3,
      kind: 'OWN_DAMAGE',
      coverageCode: 'OD',
    });
  });

  it('asks for a reason when an open exposure already exists on the coverage', async () => {
    const api = mockApi(
      viewRoutes({ claim: claim() }, [
        {
          method: 'POST',
          path: '/api/clm/v1/exposures',
          respond: (request) =>
            (request.body as { duplicateReason?: string }).duplicateReason
              ? { status: 201, body: { exposure: exposure1, claim: summary() } }
              : problem(409, 'CLM-ERR-EXPOSURE-DUPLICATE', 'Υπάρχει ήδη ανοιχτή έκθεση'),
        },
      ]),
    );
    const { user } = claimView();
    await screen.findByRole('heading', { level: 1, name: 'Ζημία CLM000000001' });
    await user.click(screen.getByRole('button', { name: 'Δημιουργία έκθεσης' }));
    expect(await screen.findByText('Αιτιολογία δεύτερης έκθεσης')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /Αιτιολογία δεύτερης έκθεσης/ }));
    await user.click(await screen.findByRole('option', { name: 'Επιπλέον ζημιά' }));
    await user.click(screen.getByRole('button', { name: 'Δημιουργία έκθεσης' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/clm/v1/exposures')).toHaveLength(2);
    });
    expect(api.callsTo('POST', '/api/clm/v1/exposures')[1]?.body).toMatchObject({
      duplicateReason: 'ADDITIONAL_DAMAGE',
    });
  });

  it('closes the claim with an outcome and explains the close-guard reasons per exposure', async () => {
    const state = { claim: claim() };
    let attempt = 0;
    const api = mockApi(
      viewRoutes(state, [
        {
          method: 'POST',
          path: '/api/clm/v1/claims/close',
          respond: () => {
            if (++attempt === 1) {
              return withErrors(422, 'CLM-ERR-CLOSE-GUARD', 'Η ζημία δεν μπορεί να κλείσει ακόμη', {
                detail:
                  'An exposure has an open reserve or a pending payment; release or settle it first.',
                'CLM000000001-01': 'OPEN_RESERVE,PAYMENT_PENDING',
              });
            }
            state.claim = claim({
              status: 'CLOSED',
              subStatus: null,
              outcome: 'COMPLETED',
              closedAt: '2026-10-08T07:00:00Z',
            });
            return { body: { claim: state.claim.summary } };
          },
        },
      ]),
    );
    const { user } = claimView();
    await screen.findByRole('heading', { level: 1, name: 'Ζημία CLM000000001' });
    const close = screen.getByRole('button', { name: 'Κλείσιμο ζημίας' });
    await user.click(close);
    expect(await screen.findByText('Επιλέξτε αποτέλεσμα.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/clm/v1/claims/close')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: /Αποτέλεσμα/ }));
    await user.click(await screen.findByRole('option', { name: 'Ολοκληρώθηκε' }));
    await user.click(close);
    const guard = await screen.findByText('Η ζημία δεν μπορεί να κλείσει ακόμη', {
      selector: '[role="alert"] *',
    });
    expect(guard).toBeInTheDocument();
    expect(within(screen.getByRole('alert')).getByText('CLM000000001-01')).toBeInTheDocument();
    expect(
      screen.getByText(
        /υπάρχει ανοιχτό αποθεματικό που πρέπει να αποδεσμευτεί; υπάρχει εκκρεμής πληρωμή/,
      ),
    ).toBeInTheDocument();

    await user.click(close);
    expect(await screen.findByText('Η ζημία είναι κλειστή')).toBeInTheDocument();
    const [first, second] = api.callsTo('POST', '/api/clm/v1/claims/close');
    expect(first?.body).toEqual({
      claimId,
      expectedRecordVersion: 3,
      outcome: 'COMPLETED',
    });
    expect(second?.headers.get('Idempotency-Key')).toBe(first?.headers.get('Idempotency-Key'));
  });

  it('reloads the claim and says so on a stale close', async () => {
    const api = mockApi(
      viewRoutes({ claim: claim() }, [
        {
          method: 'POST',
          path: '/api/clm/v1/claims/close',
          respond: () => problem(409, 'CLM-ERR-STALE', 'Η ζημία άλλαξε στο μεταξύ'),
        },
      ]),
    );
    const { user } = claimView();
    await screen.findByRole('heading', { level: 1, name: 'Ζημία CLM000000001' });
    await user.click(screen.getByRole('button', { name: /Αποτέλεσμα/ }));
    await user.click(await screen.findByRole('option', { name: 'Αποσύρθηκε' }));
    await user.click(screen.getByRole('button', { name: 'Κλείσιμο ζημίας' }));
    expect(await screen.findByText('Η ζημία δεν έκλεισε')).toBeInTheDocument();
    expect(screen.getByText(/Τα στοιχεία ανανεώθηκαν/)).toBeInTheDocument();
    await waitFor(() => {
      expect(api.callsTo('GET', `/api/clm/v1/claims/${claimId}`).length).toBeGreaterThanOrEqual(2);
    });
  });

  it('offers no close or new-exposure form on a closed claim', async () => {
    mockApi(
      viewRoutes({
        claim: claim({
          status: 'CLOSED',
          subStatus: null,
          outcome: 'NO_PAYMENT',
          closedAt: '2026-10-08T07:00:00Z',
        }),
      }),
    );
    claimView();
    expect(await screen.findByText('Η ζημία είναι κλειστή')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Κλείσιμο ζημίας' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Δημιουργία έκθεσης' })).not.toBeInTheDocument();
  });

  it('shows not found and no-permission states', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/clm/v1/claims/${claimId}`,
        respond: () => problem(404, 'CLM-ERR-NOT-FOUND', 'Δεν βρέθηκε'),
      },
    ]);
    claimView();
    expect(await screen.findByText('Η ζημία δεν βρέθηκε')).toBeInTheDocument();
    vi.unstubAllGlobals();
    mockApi([
      {
        method: 'GET',
        path: `/api/clm/v1/claims/${claimId}`,
        respond: () => problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden'),
      },
    ]);
    claimView();
    expect(await screen.findByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });
});

/* -------------------------------------------------------------- payee account */

describe('Payee bank account (claim view)', () => {
  const saved = {
    payeeAccountId: 'pa000000-1111-4222-8333-000000000001',
    verificationStatus: 'VoPMatched',
    coolingOffUntil: '2026-10-08',
    maskedIban: 'GR••••••••••••••••••••0695',
    change: false,
  };

  it('saves the account for the insured, shows only the masked IBAN and clears the fields', async () => {
    const api = mockApi(
      viewRoutes({ claim: claim() }, [
        {
          method: 'POST',
          path: '/api/bil/v1/payee-accounts',
          respond: () => ({ status: 201, body: saved }),
        },
      ]),
    );
    const { user, container } = claimView();
    await screen.findByRole('heading', { level: 1, name: 'Ζημία CLM000000001' });
    const form = screen.getByRole('form', { name: 'Τραπεζικός λογαριασμός δικαιούχου' });

    await user.click(within(form).getByRole('button', { name: 'Αποθήκευση λογαριασμού' }));
    expect(await within(form).findByText('Συμπληρώστε το IBAN.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/bil/v1/payee-accounts')).toHaveLength(0);

    await user.type(within(form).getByRole('textbox', { name: /Δικαιούχος/ }), 'Γιώργος Νικολάου');
    await user.type(
      within(form).getByRole('textbox', { name: /^IBAN/ }),
      'GR16 0110 1250 0000 0001 2300 696',
    );
    await user.click(within(form).getByRole('button', { name: 'Αποθήκευση λογαριασμού' }));
    expect(await within(form).findByText('Το IBAN δεν είναι έγκυρο.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/bil/v1/payee-accounts')).toHaveLength(0);

    const ibanField = within(form).getByRole('textbox', { name: /^IBAN/ });
    await user.clear(ibanField);
    await user.type(ibanField, iban);
    await user.click(within(form).getByRole('button', { name: 'Αποθήκευση λογαριασμού' }));
    expect(await within(form).findByText('Ο λογαριασμός αποθηκεύτηκε')).toBeInTheDocument();

    const [call] = api.callsTo('POST', '/api/bil/v1/payee-accounts');
    expect(call?.body).toEqual({
      partyId: insuredId,
      purpose: 'CLAIM_PAYMENT',
      iban,
      holderName: 'Γιώργος Νικολάου',
      source: 'STAFF',
    });
    expect(call?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    // The IBAN is in the body only: not in any URL, and not on the page after the save.
    for (const c of api.calls) expect(c.url.href).not.toContain('GR16');
    expect(within(form).getByText('GR••••••••••••••••••••0695')).toBeInTheDocument();
    expect(within(form).getByRole('textbox', { name: /^IBAN/ })).toHaveValue('');
    expect(within(form).getByRole('textbox', { name: /Δικαιούχος/ })).toHaveValue('');
    expect(document.body.textContent).not.toContain('0110125000000001230');
    await expectNoA11yViolations(container);
  });

  it('keeps the input on a failure and explains the problem', async () => {
    mockApi(
      viewRoutes({ claim: claim() }, [
        {
          method: 'POST',
          path: '/api/bil/v1/payee-accounts',
          respond: () =>
            problem(409, 'BIL-ERR-PAYEE-BLOCKED', 'Ο δικαιούχος δεν ελέγχθηκε επιτυχώς'),
        },
      ]),
    );
    const { user } = claimView();
    await screen.findByRole('heading', { level: 1, name: 'Ζημία CLM000000001' });
    const form = screen.getByRole('form', { name: 'Τραπεζικός λογαριασμός δικαιούχου' });
    await user.type(within(form).getByRole('textbox', { name: /Δικαιούχος/ }), 'Γιώργος Νικολάου');
    await user.type(within(form).getByRole('textbox', { name: /^IBAN/ }), iban);
    await user.click(within(form).getByRole('button', { name: 'Αποθήκευση λογαριασμού' }));
    expect(await within(form).findByText('Ο λογαριασμός δεν αποθηκεύτηκε')).toBeInTheDocument();
    expect(within(form).getByText(/δεν πέρασε τον έλεγχο/)).toBeInTheDocument();
    expect(within(form).getByRole('textbox', { name: /Δικαιούχος/ })).toHaveValue(
      'Γιώργος Νικολάου',
    );
  });
});

/* ---------------------------------------------------------------------- approvals */

describe('ApprovalsInboxPage', () => {
  const inbox = () =>
    renderScreen(<ApprovalsInboxPage />, { path: '/claims/approvals', url: '/claims/approvals' });

  it('lists pending approvals with type, subject, amount and maker', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/plt/v1/approval',
        respond: () => ({ body: { items: [{ request: approval() }], nextCursor: null } }),
      },
    ]);
    const { container } = inbox();
    const grid = await screen.findByRole('grid', { name: 'Εκκρεμείς εγκρίσεις' });
    expect(within(grid).getByText('Πληρωμή αποζημίωσης')).toBeInTheDocument();
    expect(within(grid).getByText('CLM · TransactionSet')).toBeInTheDocument();
    expect(within(grid).getByText('5.500,00 €')).toBeInTheDocument();
    expect(within(grid).getByText('claims')).toBeInTheDocument();
    expect(api.callsTo('GET', '/api/plt/v1/approval')[0]?.url.searchParams.get('status')).toBe(
      'PendingApproval',
    );
    await expectNoA11yViolations(container);
  });

  it('shows the empty and no-permission states', async () => {
    let denied = false;
    mockApi([
      {
        method: 'GET',
        path: '/api/plt/v1/approval',
        respond: () =>
          denied
            ? problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden')
            : { body: { items: [], nextCursor: null } },
      },
    ]);
    inbox();
    expect(await screen.findByText('Δεν υπάρχουν εκκρεμείς εγκρίσεις')).toBeInTheDocument();
    vi.unstubAllGlobals();
    denied = true;
    mockApi([
      {
        method: 'GET',
        path: '/api/plt/v1/approval',
        respond: () => problem(403, 'PLT-ERR-FORBIDDEN', 'Forbidden'),
      },
    ]);
    inbox();
    expect(await screen.findByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });
});

describe('ApprovalDetailPage', () => {
  const detailRoutes = (
    decide?: MockRoute['respond'],
    status: ApprovalView['status'] = 'PendingApproval',
  ): MockRoute[] => [
    {
      method: 'GET',
      path: `/api/plt/v1/approval/${requestId}`,
      respond: () => ({ body: { request: approval({ status }), decision: null } }),
    },
    {
      method: 'POST',
      path: '/api/plt/v1/approval/decide',
      respond:
        decide ??
        ((request) => {
          const approve = (request.body as { decision: string }).decision === 'Approve';
          return {
            body: {
              request: approval({ status: approve ? 'Approved' : 'Rejected' }),
              decision: {
                decision: approve ? 'Approved' : 'Rejected',
                checker: { kind: 'USER', id: 'claimsmgr' },
                authorityCheckId: 'ac000000-1111-4222-8333-000000000001',
                decidedAt: '2026-10-08T08:00:00Z',
              },
            },
          };
        }),
    },
  ];
  const detail = () =>
    renderScreen(<ApprovalDetailPage />, {
      path: '/claims/approvals/:requestId',
      url: `/claims/approvals/${requestId}`,
    });

  it('shows type, subject, amount, maker, reason and the diff', async () => {
    mockApi(detailRoutes());
    const { container } = detail();
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Έγκριση: Πληρωμή αποζημίωσης' }),
    ).toBeInTheDocument();
    const summaryList = screen.getByRole('region', { name: 'Στοιχεία αιτήματος' });
    expect(within(summaryList).getByText('5.500,00 €')).toBeInTheDocument();
    expect(within(summaryList).getByText('claims')).toBeInTheDocument();
    expect(within(summaryList).getByText('Πλήρης αποζημίωση επισκευής')).toBeInTheDocument();
    expect(within(summaryList).getByText(hash)).toBeInTheDocument();
    const diff = screen.getByRole('region', { name: 'Αλλαγές προς έγκριση' });
    expect(within(diff).getByText('0,00 € → 5.500,00 €')).toBeInTheDocument();
    expect(within(diff).getByText('VEHICLE_REPAIR')).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('approves with the payload hash shown and an Idempotency-Key', async () => {
    const api = mockApi(detailRoutes());
    const { user } = detail();
    await screen.findByRole('heading', { level: 1 });
    await user.type(
      screen.getByRole('textbox', { name: /Σχόλιο/ }),
      'Σύμφωνα με την πραγματογνωμοσύνη',
    );
    await user.click(screen.getByRole('button', { name: 'Έγκριση' }));
    expect(await screen.findByText('Το αίτημα εγκρίθηκε')).toBeInTheDocument();
    const [call] = api.callsTo('POST', '/api/plt/v1/approval/decide');
    expect(call?.body).toEqual({
      requestId,
      decision: 'Approve',
      payloadHash: hash,
      comment: 'Σύμφωνα με την πραγματογνωμοσύνη',
    });
    expect(call?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('needs a comment to reject', async () => {
    const api = mockApi(detailRoutes());
    const { user } = detail();
    await screen.findByRole('heading', { level: 1 });
    await user.click(screen.getByRole('button', { name: 'Απόρριψη' }));
    expect(await screen.findByText('Γράψτε σχόλιο για την απόρριψη.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/plt/v1/approval/decide')).toHaveLength(0);
    await user.type(screen.getByRole('textbox', { name: /Σχόλιο/ }), 'Λείπουν παραστατικά');
    await user.click(screen.getByRole('button', { name: 'Απόρριψη' }));
    expect(await screen.findByText('Το αίτημα απορρίφθηκε')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/plt/v1/approval/decide')[0]?.body).toMatchObject({
      decision: 'Reject',
      comment: 'Λείπουν παραστατικά',
    });
  });

  it('refreshes the request and says so when the decision is stale (409)', async () => {
    const api = mockApi(
      detailRoutes(() =>
        problem(409, 'PLT-ERR-APPROVAL-STALE', 'Το αίτημα δεν είναι πλέον σε ισχύ'),
      ),
    );
    const { user } = detail();
    await screen.findByRole('heading', { level: 1 });
    await user.click(screen.getByRole('button', { name: 'Έγκριση' }));
    expect(await screen.findByText('Το αίτημα άλλαξε')).toBeInTheDocument();
    await waitFor(() => {
      expect(api.callsTo('GET', `/api/plt/v1/approval/${requestId}`).length).toBeGreaterThanOrEqual(
        2,
      );
    });
  });

  it.each([
    ['PLT-ERR-SELF-APPROVAL', 403, /Δεν μπορείτε να εγκρίνετε αίτημα που υποβάλατε εσείς/],
    [
      'PLT-ERR-EDITOR-CANNOT-APPROVE',
      403,
      /Όποιος έχει επεξεργαστεί το αίτημα δεν μπορεί να το εγκρίνει/,
    ],
    ['PLT-ERR-AUTHORITY-DENIED', 403, /όριο εξουσιοδότησής σας δεν επαρκεί/],
    ['PLT-ERR-AUTHORITY-REFERRAL-REQUIRED', 409, /πρέπει να παραπεμφθεί σε ανώτερο εγκριτή/],
  ])('explains %s clearly', async (code, status, message) => {
    mockApi(detailRoutes(() => problem(status, code, 'Σφάλμα')));
    const { user } = detail();
    await screen.findByRole('heading', { level: 1 });
    await user.click(screen.getByRole('button', { name: 'Έγκριση' }));
    expect(await screen.findByText('Η απόφαση δεν καταγράφηκε')).toBeInTheDocument();
    expect(screen.getByText(message)).toBeInTheDocument();
  });

  it('offers no decision on a request that is no longer pending', async () => {
    mockApi(detailRoutes(undefined, 'Approved'));
    detail();
    expect(await screen.findByText('Το αίτημα δεν περιμένει πλέον απόφαση')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Έγκριση' })).not.toBeInTheDocument();
  });

  it('shows not found', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/plt/v1/approval/${requestId}`,
        respond: () => problem(404, 'PLT-ERR-NOT-FOUND', 'Δεν βρέθηκε'),
      },
    ]);
    detail();
    expect(await screen.findByText('Το αίτημα δεν βρέθηκε')).toBeInTheDocument();
  });
});

describe('English', () => {
  it('has every claims string translated', async () => {
    const { default: i18n } = await import('../../i18n');
    await i18n.changeLanguage('en');
    try {
      mockApi([
        {
          method: 'GET',
          path: '/api/plt/v1/approval',
          respond: () => ({ body: { items: [], nextCursor: null } }),
        },
      ]);
      renderScreen(<ApprovalsInboxPage />, { path: '/claims/approvals', url: '/claims/approvals' });
      expect(await screen.findByText('No pending approvals')).toBeInTheDocument();
    } finally {
      await i18n.changeLanguage('el');
    }
  });
});
