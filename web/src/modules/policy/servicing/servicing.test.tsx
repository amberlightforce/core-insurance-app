import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { PolicyGetResponse } from '../../../api/types';
import { expectNoA11yViolations } from '../../../test/axe';
import * as fx from '../../../test/fixtures';
import { mockApi, problem, renderScreen, type MockRoute } from '../../../test/mockApi';
import { addDays, athensToday } from '../../quote/time';
import type { ServicingPreview } from './api';
import { CancellationPage } from './CancellationPage';
import { ChangeWorkspacePage } from './ChangeWorkspacePage';
import { dueOf, vehicleDiff, formOf } from './logic';
import { RenewalPage } from './RenewalPage';
import { ServicingPreviewView } from './ServicingPreviewView';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const eur = (amount: string) => ({ amount, currency: 'EUR' });
const jobId = fx.jobId;

function policyFx(
  state: 'IN_FORCE' | 'SCHEDULED' | 'EXPIRED' | 'CANCELLED' = 'IN_FORCE',
  daysIntoTerm = 120,
): PolicyGetResponse {
  const base = fx.policy(state === 'CANCELLED' || state === 'EXPIRED' ? 'EXPIRED' : state);
  const from = addDays(athensToday(), -daysIntoTerm);
  const to = addDays(from, 365);
  return {
    ...base,
    policy: { ...base.policy, status: state },
    term: base.term
      ? {
          ...base.term,
          state,
          period: { from: `${from}T00:00:00+03:00`, to: `${to}T00:00:00+03:00` },
        }
      : base.term,
    riskTree: {
      vehicles: [
        {
          locator: 'v1',
          plate: 'ΙΚΧ1234',
          make: 'Toyota',
          model: 'Yaris',
          firstRegistrationYear: 2021,
          engineCapacityCc: 1500,
          use: 'PRIVATE',
          value: eur('12000.00'),
          fields: { garagingPostcode: '10431', ownerType: 'PERSON' },
        },
      ],
      drivers: [],
      coverages: [
        { coverageCode: 'MTPL', selected: true, elementLocator: 'v1' },
        { coverageCode: 'OWN-DAMAGE', selected: true, elementLocator: 'v1' },
      ],
      questionSets: [],
    },
  } as unknown as PolicyGetResponse;
}

const policyRoute = (data: PolicyGetResponse): MockRoute => ({
  method: 'GET',
  path: `/api/pol/v1/policies/${fx.policyId}`,
  respond: () => ({ body: data }),
});

const catalogueRoute: MockRoute = {
  method: 'GET',
  path: `/api/pfc/v1/catalogue/${fx.hash}`,
  respond: () => ({ body: fx.catalogue }),
};

const jobRoute: MockRoute = {
  method: 'GET',
  path: `/api/pol/v1/jobs/${jobId}`,
  respond: () => ({
    body: { job: { jobId, currentVersionNo: 1, versions: [{ versionNo: 1, draftVersion: 0 }] } },
  }),
};

function cancellationPreview(): ServicingPreview {
  return {
    annualBefore: eur('430.00'),
    annualAfter: eur('0.00'),
    proratedLines: [
      {
        elementLocator: 'v1',
        coverageCode: 'MTPL',
        chargeType: 'PREM-MTPL',
        chargeCategory: 'PREMIUM',
        period: { from: '2027-02-05', to: '2027-10-08' },
        days: 245,
        termDays: 365,
        fraction: '0.6712',
        annualAmount: eur('430.00'),
        amount: eur('-288.63'),
      },
    ],
    taxLines: [
      {
        elementLocator: 'v1',
        chargeType: 'GR-IPT',
        chargeCategory: 'TAX',
        amount: eur('0.00'),
        treatmentAction: 'KEEP_NOT_REDUCED',
        ruleId: 'GR.IPT.CANCEL',
        ruleVersion: '1',
        legalStatus: 'PendingOpinion',
        provisional: true,
      },
    ],
    premiumChange: eur('-288.63'),
    taxChange: eur('0.00'),
    totalChange: eur('-288.63'),
    refundDue: eur('288.63'),
    additionalDue: eur('0.00'),
    transactionKind: 'CANCELLATION',
    cancellationSource: 'Policyholder',
    refundMethod: 'ProRata',
    provisional: true,
  };
}

function changePreview(additional: boolean): ServicingPreview {
  return {
    ...cancellationPreview(),
    proratedLines: cancellationPreview().proratedLines.map((l) => ({
      ...l,
      amount: eur(additional ? '25.00' : '-25.00'),
    })),
    taxLines: [
      {
        elementLocator: 'v1',
        chargeType: 'GR-IPT',
        chargeCategory: 'TAX',
        amount: eur(additional ? '3.75' : '0.00'),
        treatmentAction: additional ? 'APPLY' : 'KEEP_NOT_REDUCED',
        ruleId: 'GR.IPT',
        ruleVersion: '1',
        legalStatus: additional ? 'Settled' : 'PendingOpinion',
        provisional: !additional,
      },
    ],
    premiumChange: eur(additional ? '25.00' : '-25.00'),
    taxChange: eur(additional ? '3.75' : '0.00'),
    totalChange: eur(additional ? '28.75' : '-25.00'),
    refundDue: eur(additional ? '0.00' : '25.00'),
    additionalDue: eur(additional ? '28.75' : '0.00'),
    transactionKind: additional ? 'ENDORSEMENT_DEBIT' : 'ENDORSEMENT_CREDIT',
    provisional: !additional,
  };
}

describe('dueOf and vehicleDiff', () => {
  it('tells a refund from an additional amount', () => {
    expect(dueOf(cancellationPreview()).kind).toBe('refund');
    expect(dueOf(changePreview(true)).kind).toBe('additional');
    expect(
      dueOf({ ...changePreview(true), additionalDue: eur('0.00'), refundDue: eur('0.00') }).kind,
    ).toBe('neutral');
  });

  it('lists only the vehicle details that differ', () => {
    const before = formOf(policyFx().riskTree?.vehicles[0]);
    const after = { ...before, engineCapacityCc: '1800', value: '15000.00' };
    expect(vehicleDiff(before, after).map((r) => r.field)).toEqual(['engineCapacityCc', 'value']);
    expect(before.garagingPostcode).toBe('10431');
  });
});

describe('ServicingPreviewView', () => {
  it('shows days, fraction and amount per prorated line and marks the provisional tax line', async () => {
    const { container } = renderScreen(<ServicingPreviewView preview={cancellationPreview()} />, {
      path: '/',
      url: '/',
    });
    expect(await screen.findByText('Επιστροφή προς τον πελάτη')).toBeInTheDocument();
    expect(screen.getAllByText('288,63 €').length).toBeGreaterThan(0);
    const prorated = screen.getByRole('grid', { name: 'Αναλογικές γραμμές ασφαλίστρου' });
    expect(within(prorated).getByText('245 / 365 · 0.6712')).toBeInTheDocument();
    const tax = screen.getByRole('grid', { name: 'Γραμμές φόρων' });
    expect(within(tax).getByText('ΦΑΑ: δεν επιστρέφεται')).toBeInTheDocument();
    expect(within(tax).getByText(/Προσωρινό/)).toBeInTheDocument();
    expect(screen.getByText('Προσωρινή φορολογική μεταχείριση')).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('shows the additional amount due for a debit and no provisional banner when settled', async () => {
    renderScreen(<ServicingPreviewView preview={changePreview(true)} />, { path: '/', url: '/' });
    expect(
      await screen.findByText('Πρόσθετο ποσό προς είσπραξη από τον πελάτη'),
    ).toBeInTheDocument();
    expect(screen.queryByText('Προσωρινή φορολογική μεταχείριση')).not.toBeInTheDocument();
  });
});

describe('CancellationPage', () => {
  const page = () =>
    renderScreen(<CancellationPage />, {
      path: '/policies/:policyId/cancel',
      url: `/policies/${fx.policyId}/cancel`,
    });

  const cancelRoutes = (overrides: { bind?: MockRoute['respond'] } = {}): MockRoute[] => [
    policyRoute(policyFx()),
    catalogueRoute,
    {
      method: 'POST',
      path: '/api/pol/v1/cancellations',
      respond: () => ({
        status: 201,
        body: {
          jobId,
          state: 'DRAFT',
          kind: 'STANDARD',
          effectiveAt: '2027-02-05T10:00:00Z',
          servicingPreview: cancellationPreview(),
        },
      }),
    },
    jobRoute,
    {
      method: 'POST',
      path: '/api/pol/v1/jobs/quote',
      respond: () => ({
        body: { ...fx.quote(), servicingPreview: cancellationPreview() },
      }),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/jobs/bind',
      respond:
        overrides.bind ??
        (() => ({
          body: {
            jobId,
            state: 'BOUND',
            gateResults: [{ gate: 'EFFECTIVE_DATE', passed: true, severity: 'BLOCK' }],
            servicingPreview: cancellationPreview(),
          },
        })),
    },
  ];

  it('requires the reason, then previews the refund and cancels after explicit confirmation', async () => {
    const api = mockApi(cancelRoutes());
    const { user, container } = page();
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Ακύρωση ασφαλιστηρίου' }),
    ).toBeInTheDocument();
    expect(screen.getByText(/Επιλέξτε την αιτία και δείτε/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Προεπισκόπηση επιστροφής' }));
    expect(await screen.findByText('Επιλέξτε την αιτία της ακύρωσης.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/pol/v1/cancellations')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: /Αιτία/ }));
    await user.click(await screen.findByRole('option', { name: 'Αίτημα πελάτη' }));
    await user.click(screen.getByRole('button', { name: 'Προεπισκόπηση επιστροφής' }));

    expect(await screen.findByText('Επιστροφή προς τον πελάτη')).toBeInTheDocument();
    const created = api.callsTo('POST', '/api/pol/v1/cancellations')[0];
    expect(created?.body).toMatchObject({
      policyId: fx.policyId,
      source: 'Policyholder',
      reasonCode: 'CUSTOMER_REQUEST',
      kind: 'STANDARD',
    });
    expect(created?.headers.get('Idempotency-Key')).toBeTruthy();
    expect(api.callsTo('POST', '/api/pol/v1/jobs/quote')[0]?.body).toEqual({ jobId, versionNo: 1 });
    expect(screen.getByText(/ΦΑΑ: δεν επιστρέφεται/)).toBeInTheDocument();
    expect(await screen.findByText('Αστική ευθύνη αυτοκινήτου')).toBeInTheDocument();
    expect(screen.getByText('Ετήσιο')).toBeInTheDocument();
    await expectNoA11yViolations(container);

    await user.click(screen.getByRole('button', { name: 'Ακύρωση ασφαλιστηρίου' }));
    const dialog = await screen.findByRole('alertdialog');
    const confirm = within(dialog).getByRole('button', { name: 'Ακύρωση ασφαλιστηρίου' });
    await user.click(confirm);
    expect(api.callsTo('POST', '/api/pol/v1/jobs/bind')).toHaveLength(0);
    await user.click(within(dialog).getByRole('checkbox'));
    await user.click(within(dialog).getByRole('button', { name: 'Ακύρωση ασφαλιστηρίου' }));

    expect((await screen.findAllByText('Το ασφαλιστήριο ακυρώθηκε')).length).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/pol/v1/jobs/bind')[0]?.body).toMatchObject({
      jobId,
      versionNo: 1,
      confirmation: true,
      paymentPlanOption: 'ANNUAL',
    });
  });

  it('explains an out-of-sequence refusal in plain words', async () => {
    // The first matching route wins, so the refusal comes first.
    mockApi([
      {
        method: 'POST',
        path: '/api/pol/v1/cancellations',
        respond: () => problem(422, 'POL-ERR-OUT-OF-SEQUENCE', 'Out of sequence'),
      },
      policyRoute(policyFx()),
    ]);
    const { user } = page();
    await screen.findByRole('heading', { level: 1, name: 'Ακύρωση ασφαλιστηρίου' });
    await user.click(screen.getByRole('button', { name: /Αιτία/ }));
    await user.click(await screen.findByRole('option', { name: 'Πώληση οχήματος' }));
    await user.click(screen.getByRole('button', { name: 'Προεπισκόπηση επιστροφής' }));
    expect(
      await screen.findByText(/Υπάρχει ήδη μεταγενέστερη συναλλαγή στην περίοδο/),
    ).toBeInTheDocument();
    expect(screen.getByText('POL-ERR-OUT-OF-SEQUENCE')).toBeInTheDocument();
  });

  it('shows the no-permission state when the API answers 403', async () => {
    mockApi([
      policyRoute(policyFx()),
      {
        method: 'POST',
        path: '/api/pol/v1/cancellations',
        respond: () => ({ status: 403 }),
      },
    ]);
    const { user } = page();
    await screen.findByRole('heading', { level: 1, name: 'Ακύρωση ασφαλιστηρίου' });
    await user.click(screen.getByRole('button', { name: /Αιτία/ }));
    await user.click(await screen.findByRole('option', { name: 'Αίτημα πελάτη' }));
    await user.click(screen.getByRole('button', { name: 'Προεπισκόπηση επιστροφής' }));
    expect(
      await screen.findByText(/Ο ρόλος σας δεν επιτρέπει αυτή την ενέργεια/),
    ).toBeInTheDocument();
  });

  it('shows the empty state for a policy that is already expired and the error state when it fails to load', async () => {
    mockApi([policyRoute(policyFx('EXPIRED', 400))]);
    page();
    expect(await screen.findByText('Το ασφαλιστήριο δεν μπορεί να ακυρωθεί')).toBeInTheDocument();
  });

  it('offers a flat cancellation only for a scheduled term', async () => {
    mockApi([policyRoute(policyFx('SCHEDULED', -30))]);
    page();
    await screen.findByRole('heading', { level: 1, name: 'Ακύρωση ασφαλιστηρίου' });
    expect(screen.getByRole('radio', { name: /Επί της αρχής/ })).toBeChecked();
  });
});

describe('RenewalPage', () => {
  const page = () =>
    renderScreen(<RenewalPage />, {
      path: '/policies/:policyId/renew',
      url: `/policies/${fx.policyId}/renew`,
    });

  const renewPreview = (): ServicingPreview => ({
    ...changePreview(true),
    transactionKind: 'NEW_BUSINESS',
    annualBefore: eur('430.00'),
    annualAfter: eur('445.00'),
  });

  const renewRoutes = (
    quoteBody: object = { ...fx.quote(), servicingPreview: renewPreview() },
  ): MockRoute[] => [
    policyRoute(policyFx('IN_FORCE', 340)),
    catalogueRoute,
    {
      method: 'POST',
      path: '/api/pol/v1/renewals',
      respond: () => ({
        status: 201,
        body: { jobId, state: 'DRAFT', expiringTermId: 'term-1' },
      }),
    },
    jobRoute,
    { method: 'POST', path: '/api/pol/v1/jobs/quote', respond: () => ({ body: quoteBody }) },
    {
      method: 'POST',
      path: '/api/pol/v1/renewals/offer',
      respond: () => ({
        body: {
          jobId,
          state: 'QUOTED',
          offerVersion: 1,
          premiumSummary: { premium: eur('445.00'), taxes: eur('50.00'), total: eur('495.00') },
          acceptanceMode: 'EXPLICIT',
          deadline: '2027-10-08T00:00:00Z',
        },
      }),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/renewals/accept',
      respond: () => ({
        body: {
          jobId,
          state: 'SCHEDULED',
          newTermId: 'term-2',
          newTermNumber: 2,
          predecessorTermId: 'term-1',
          transactionId: 'tx-9',
          termState: 'SCHEDULED',
        },
      }),
    },
  ];

  it('renews now, shows the rated renewal, offers it and records the explicit acceptance', async () => {
    const api = mockApi(renewRoutes());
    const { user, container } = page();
    expect(await screen.findByRole('heading', { level: 1, name: 'Ανανέωση' })).toBeInTheDocument();
    expect(screen.getByText(/Χρησιμοποιήστε το Ανανέωση τώρα/)).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Ανανέωση τώρα' }));
    expect(await screen.findByText('Τιμολογήθηκε')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/pol/v1/renewals')[0]?.body).toEqual({ termId: 'term-1' });
    expect(api.callsTo('POST', '/api/pol/v1/jobs/quote')[0]?.body).toEqual({ jobId, versionNo: 1 });
    expect(screen.getByText('Πρόσθετο ποσό προς είσπραξη από τον πελάτη')).toBeInTheDocument();
    await expectNoA11yViolations(container);

    await user.click(screen.getByRole('button', { name: 'Έκδοση προσφοράς' }));
    expect((await screen.findAllByText('Η προσφορά εκδόθηκε')).length).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/pol/v1/renewals/offer')[0]?.body).toEqual({
      jobId,
      termId: 'term-1',
    });

    await user.click(screen.getByRole('button', { name: 'Καταχώριση αποδοχής' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox'));
    await user.click(within(dialog).getByRole('button', { name: 'Καταχώριση αποδοχής' }));
    expect((await screen.findAllByText('Η ανανέωση έγινε αποδεκτή')).length).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/pol/v1/renewals/accept')[0]?.body).toMatchObject({
      jobId,
      termId: 'term-1',
      channel: 'STAFF',
    });
  });

  it('blocks the offer while the renewal is referred and says why', async () => {
    mockApi(
      renewRoutes({
        ...fx.quote({ decision: 'REFER', referred: true, bindable: false }),
        servicingPreview: renewPreview(),
      }),
    );
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: 'Ανανέωση τώρα' }));
    expect(await screen.findByText('Παραπέμφθηκε στην ανάληψη')).toBeInTheDocument();
    const offer = screen.getByRole('button', { name: 'Έκδοση προσφοράς' });
    await user.click(offer);
    expect(screen.queryByText('Η προσφορά εκδόθηκε')).not.toBeInTheDocument();
  });

  it('asks to re-offer when the policy moved after the offer', async () => {
    const routes = renewRoutes();
    mockApi([
      {
        method: 'POST',
        path: '/api/pol/v1/renewals/accept',
        respond: () => problem(409, 'POL-ERR-REBASE-REQUIRED', 'Rebase required'),
      },
      ...routes,
    ]);
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: 'Ανανέωση τώρα' }));
    await user.click(await screen.findByRole('button', { name: 'Έκδοση προσφοράς' }));
    await user.click(await screen.findByRole('button', { name: 'Καταχώριση αποδοχής' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox'));
    await user.click(within(dialog).getByRole('button', { name: 'Καταχώριση αποδοχής' }));
    expect(await screen.findByText(/Το ασφαλιστήριο άλλαξε μετά την προσφορά/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Νέα έκδοση προσφοράς' })).toBeInTheDocument();
  });

  it('shows the empty state for a term that is not in force', async () => {
    mockApi([policyRoute(policyFx('SCHEDULED', -30))]);
    page();
    expect(
      await screen.findByText('Το ασφαλιστήριο δεν μπορεί να ανανεωθεί τώρα'),
    ).toBeInTheDocument();
  });
});

describe('ChangeWorkspacePage', () => {
  const page = () =>
    renderScreen(<ChangeWorkspacePage />, {
      path: '/policies/:policyId/change',
      url: `/policies/${fx.policyId}/change`,
    });

  const changeRoutes = (
    overrides: { create?: MockRoute['respond']; preview?: ServicingPreview } = {},
  ): MockRoute[] => [
    policyRoute(policyFx()),
    catalogueRoute,
    {
      method: 'POST',
      path: '/api/pol/v1/policy-changes',
      respond:
        overrides.create ??
        (() => ({
          status: 201,
          body: { jobId, state: 'DRAFT', termId: 'term-1', baseTransactionId: 'tx-1' },
        })),
    },
    jobRoute,
    {
      method: 'POST',
      path: '/api/pol/v1/jobs/update-draft',
      respond: (r) => {
        const body = r.body as { versionNo: number; expectedDraftVersion: number };
        return {
          body: {
            jobId,
            versionNo: body.versionNo,
            draftVersion: body.expectedDraftVersion + 1,
            state: 'DRAFT',
            riskTree: {
              vehicles: [{ locator: 'v1', plate: 'ΙΚΧ1234' }],
              drivers: [],
              coverages: [],
              questionSets: [],
            },
            validation: [],
          },
        };
      },
    },
    {
      method: 'POST',
      path: '/api/pol/v1/jobs/quote',
      respond: () => ({
        body: { ...fx.quote(), servicingPreview: overrides.preview ?? changePreview(true) },
      }),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/jobs/bind',
      respond: () => ({
        body: {
          jobId,
          state: 'BOUND',
          gateResults: [{ gate: 'EFFECTIVE_DATE', passed: true, severity: 'BLOCK' }],
          servicingPreview: changePreview(true),
        },
      }),
    },
  ];

  const next = (user: ReturnType<typeof page>['user']) =>
    user.click(screen.getByRole('button', { name: /Επόμενο/ }));

  it('walks date, vehicle edit, premium preview with diff, and the explicit confirmation', async () => {
    const api = mockApi(changeRoutes());
    const { user, container } = page();
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Αλλαγή εντός περιόδου' }),
    ).toBeInTheDocument();
    expect(screen.getByText(/Επιτρεπόμενες ημερομηνίες/)).toBeInTheDocument();

    await next(user);
    // Nothing is started before the first step is left; then the job is created for the chosen instant.
    await screen.findByRole('textbox', { name: /Έτος πρώτης κυκλοφορίας/ });
    const created = api.callsTo('POST', '/api/pol/v1/policy-changes')[0];
    expect(created?.body).toMatchObject({ policyId: fx.policyId });
    expect(created?.headers.get('Idempotency-Key')).toBeTruthy();

    // Required inputs are required: the garaging postcode is on the policy, so clear it and the step blocks.
    const engine = screen.getByRole('textbox', { name: /Κυβισμός/ });
    await user.clear(engine);
    expect(screen.getByRole('button', { name: /Επόμενο/ })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await user.type(engine, '1800');
    await next(user);

    await user.click(
      await screen.findByRole('button', { name: 'Υπολογισμός προεπισκόπησης ασφαλίστρου' }),
    );
    expect(
      await screen.findByText('Πρόσθετο ποσό προς είσπραξη από τον πελάτη'),
    ).toBeInTheDocument();
    const update = api.callsTo('POST', '/api/pol/v1/jobs/update-draft')[0]?.body as {
      instructions: { op: string; vehicle: { locator: string; engineCapacityCc: number } }[];
    };
    expect(update.instructions[0]).toMatchObject({
      op: 'SET_VEHICLE',
      vehicle: { locator: 'v1', engineCapacityCc: 1800 },
    });
    const diff = screen.getByRole('grid', { name: 'Αλλαγμένα στοιχεία οχήματος' });
    expect(within(diff).getByText('1500')).toBeInTheDocument();
    expect(within(diff).getByText('1800')).toBeInTheDocument();
    await expectNoA11yViolations(container);

    await next(user);
    await user.click(await screen.findByRole('button', { name: 'Εφαρμογή αλλαγής' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox'));
    await user.click(within(dialog).getByRole('button', { name: 'Εφαρμογή αλλαγής' }));
    expect((await screen.findAllByText('Η αλλαγή εφαρμόστηκε')).length).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/pol/v1/jobs/bind')[0]?.body).toMatchObject({
      jobId,
      confirmation: true,
    });
  });

  it('explains a refused effective date with the permitted range and keeps the user on the first step', async () => {
    mockApi(
      changeRoutes({
        create: () => problem(422, 'POL-ERR-EFFDATE-LIMIT', 'Effective date outside limits'),
      }),
    );
    const { user } = page();
    await screen.findByRole('heading', { level: 1, name: 'Αλλαγή εντός περιόδου' });
    await next(user);
    expect(
      await screen.findByText(
        'Η ημερομηνία ισχύος είναι εκτός των ημερομηνιών που επιτρέπονται στον ρόλο σας.',
      ),
    ).toBeInTheDocument();
    expect(
      screen.getByText('Ημερομηνία ισχύος', { selector: 'label *, label' }),
    ).toBeInTheDocument();
  });

  it('explains a rating input the engine refused and takes the user to the vehicle step', async () => {
    const routes = changeRoutes();
    mockApi([
      {
        method: 'POST',
        path: '/api/pol/v1/jobs/quote',
        respond: () =>
          problem(
            422,
            'POL-ERR-RATING',
            'Rating failed',
            'RAT-ERR-INPUT: vehicle.vehicleValue is required to rate OWN-DAMAGE',
          ),
      },
      ...routes,
    ]);
    const { user } = page();
    await screen.findByRole('heading', { level: 1, name: 'Αλλαγή εντός περιόδου' });
    await next(user);
    const engine = await screen.findByRole('textbox', { name: /Κυβισμός/ });
    await user.clear(engine);
    await user.type(engine, '1700');
    await next(user);
    await user.click(
      await screen.findByRole('button', { name: 'Υπολογισμός προεπισκόπησης ασφαλίστρου' }),
    );
    expect(await screen.findByText(/Λείπει η αξία του οχήματος/)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Μετάβαση στο βήμα οχήματος' }));
    await waitFor(() => {
      expect(screen.getByRole('textbox', { name: /Κυβισμός/ })).toBeInTheDocument();
    });
  });

  it('shows the empty state when the term cannot be changed', async () => {
    mockApi([policyRoute(policyFx('EXPIRED', 400))]);
    page();
    expect(
      await screen.findByText('Το ασφαλιστήριο δεν μπορεί να τροποποιηθεί'),
    ).toBeInTheDocument();
  });

  it('shows a loading state, then the error state with a retry when the policy cannot be read', async () => {
    mockApi([
      {
        method: 'GET',
        path: `/api/pol/v1/policies/${fx.policyId}`,
        respond: () => problem(500, 'PLT-ERR-INTERNAL', 'Something broke'),
      },
    ]);
    page();
    expect(await screen.findByText('Something broke')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Δοκιμή ξανά|Επανάληψη/ })).toBeInTheDocument();
  });
});
