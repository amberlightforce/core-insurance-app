import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { JobBindResponse } from '../../api/types';
import { expectNoA11yViolations } from '../../test/axe';
import * as fx from '../../test/fixtures';
import { mockApi, problem, renderScreen, type MockRoute } from '../../test/mockApi';
import { QuoteWizardPage } from './QuoteWizardPage';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

type User = ReturnType<typeof renderScreen>['user'];

function routes(
  overrides: {
    bind?: () => { status?: number; body?: unknown };
    quote?: () => { status?: number; body?: unknown };
  } = {},
): MockRoute[] {
  return [
    {
      method: 'GET',
      path: `/api/pty/v1/parties/${fx.partyId}`,
      respond: () => ({ body: { party: fx.party() } }),
    },
    {
      method: 'POST',
      path: '/api/pfc/v1/product-versions/resolve',
      respond: () => ({ body: fx.resolved }),
    },
    {
      method: 'GET',
      path: `/api/pfc/v1/catalogue/${fx.hash}`,
      respond: () => ({ body: fx.catalogue }),
    },
    {
      method: 'GET',
      path: `/api/pfc/v1/question-sets/${fx.hash}`,
      respond: () => ({ body: fx.questionSet }),
    },
    {
      method: 'POST',
      path: '/api/pfc/v1/question-sets/evaluate',
      respond: () => ({
        body: { questions: [], knockOuts: [], referrals: [], missingRequired: [], complete: true },
      }),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/submissions',
      respond: () => ({
        status: 201,
        body: {
          jobId: fx.jobId,
          jobNumber: 'Q1',
          policyId: fx.policyId,
          state: 'DRAFT',
          productVersion: '1.0',
          expirationAt: '2027-10-08T00:00:00Z',
          versionNo: 1,
          manifest: { artefactHash: fx.hash },
        },
      }),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/jobs/update-draft',
      respond: (r) => {
        const body = r.body as {
          versionNo: number;
          expectedDraftVersion: number;
          instructions: { op: string }[];
        };
        const hasVehicle = body.instructions.some((i) => i.op === 'SET_VEHICLE');
        return {
          body: {
            jobId: fx.jobId,
            versionNo: body.versionNo,
            draftVersion: body.expectedDraftVersion + 1,
            state: 'DRAFT',
            riskTree: {
              vehicles: [{ locator: 'veh-1', plate: 'ΙΚΧ1234' }],
              drivers: hasVehicle
                ? []
                : [{ locator: 'drv-1', partyId: fx.partyId, driverType: 'MAIN' }],
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
      respond: overrides.quote ?? (() => ({ body: fx.quote() })),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/jobs/bind',
      respond: overrides.bind ?? (() => ({ body: fx.bound() })),
    },
  ];
}

const next = (user: User) => user.click(screen.getByRole('button', { name: /Επόμενο/ }));

async function chooseOption(user: User, label: RegExp, option: string) {
  await user.click(screen.getByRole('button', { name: label }));
  await user.click(await screen.findByRole('option', { name: option }));
}

/** Walks policyholder → vehicle → driver and lands on the covers step. */
async function walkToCovers(user: User) {
  const nextButton = await screen.findByRole('button', { name: /Επόμενο/ });
  await waitFor(() => {
    expect(screen.getAllByText('Διεπαφής Δοκιμή').length).toBeGreaterThan(0);
    expect(screen.getByText('MOTOR-GR')).toBeInTheDocument();
    expect(nextButton).not.toHaveAttribute('aria-disabled', 'true');
  });
  await next(user);

  await screen.findByRole('heading', { level: 2, name: 'Όχημα' });
  await user.type(screen.getByRole('textbox', { name: /Αριθμός κυκλοφορίας/ }), 'IKX1234');
  await user.type(screen.getByRole('textbox', { name: /Μάρκα/ }), 'Toyota');
  await user.type(screen.getByRole('textbox', { name: /Μοντέλο/ }), 'Yaris');
  await user.type(screen.getByRole('textbox', { name: /Έτος πρώτης κυκλοφορίας/ }), '2021');
  await user.type(screen.getByRole('textbox', { name: /Κυβισμός/ }), '1400');
  await user.type(
    screen.getByRole('textbox', { name: /Ταχυδρομικός κώδικας στάθμευσης/ }),
    '11526',
  );
  await next(user);

  await screen.findByRole('heading', { level: 2, name: 'Οδηγός' });
  await user.type(screen.getByRole('textbox', { name: /Έτος απόκτησης διπλώματος/ }), '2010');
  await next(user);

  await screen.findByRole('heading', { level: 2, name: 'Καλύψεις' });
}

/** Walks policyholder → vehicle → driver → covers → questions and lands on the premium step. */
async function walkToPremium(user: User) {
  await walkToCovers(user);
  await chooseOption(user, /Σωματικές βλάβες ανά άτομο/, '1.300.000 €');
  await next(user);

  await screen.findByRole('heading', { level: 2, name: 'Ερωτήσεις κινδύνου' });
  await user.click(screen.getByRole('radio', { name: 'Ιδιωτική χρήση' }));
  await user.click(screen.getByRole('radio', { name: 'Όχι' }));
  await next(user);
  await screen.findByRole('heading', { level: 2, name: 'Ασφάλιστρο' });
}

const url = `/policies/quotes/new?partyId=${fx.partyId}`;

describe('QuoteWizardPage', () => {
  it('walks the wizard, writes the draft, quotes with warnings and binds with explicit confirmation (ANNUAL)', async () => {
    const api = mockApi(routes());
    const { user, container } = renderScreen(<QuoteWizardPage />, {
      path: '/policies/quotes/new',
      url,
    });

    await walkToPremium(user);
    await expectNoA11yViolations(container);
    // Not yet calculated: Next explains why.
    expect(screen.getByRole('button', { name: /Επόμενο/ })).toHaveAttribute(
      'aria-disabled',
      'true',
    );

    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    expect(await screen.findByText('Ενδεικτικό τιμολόγιο')).toBeInTheDocument();
    expect(screen.getByText('Προσωρινοί φόροι και εισφορές')).toBeInTheDocument();
    expect(screen.getAllByText('Αποδοχή').length).toBeGreaterThan(0);
    expect(screen.getAllByText('479,20 €').length).toBeGreaterThan(0);

    // The commands: one submission, two draft writes (vehicle first, then driver/covers/answers), one quote.
    const submission = api.callsTo('POST', '/api/pol/v1/submissions')[0];
    expect(submission?.body).toMatchObject({
      policyholderPartyId: fx.partyId,
      product: 'MOTOR-GR',
      channel: 'STAFF',
      quoteType: 'FULL',
    });
    const drafts = api.callsTo('POST', '/api/pol/v1/jobs/update-draft');
    expect(drafts).toHaveLength(2);
    const [vehicleCall, riskCall] = drafts.map(
      (d) =>
        d.body as {
          expectedDraftVersion: number;
          instructions: { op: string; driver?: { vehicleLocator: string } }[];
        },
    );
    expect(vehicleCall?.instructions.map((i) => i.op)).toEqual(['SET_VEHICLE']);
    expect(riskCall?.expectedDraftVersion).toBe(1);
    expect(riskCall?.instructions.map((i) => i.op)).toEqual([
      'SET_DRIVER',
      'SET_COVERAGES',
      'SET_ANSWERS',
    ]);
    expect(riskCall?.instructions[0]?.driver?.vehicleLocator).toBe('veh-1');
    for (const call of [submission, ...drafts, api.callsTo('POST', '/api/pol/v1/jobs/quote')[0]]) {
      expect(call?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    }

    await next(user);
    await screen.findByRole('heading', { level: 2, name: 'Δέσμευση' });
    expect(screen.getByRole('radio', { name: /Ετήσιο \(ANNUAL\)/ })).toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Δέσμευση' }));
    const dialog = await screen.findByRole('dialog', { name: 'Επιβεβαίωση δέσμευσης' });
    const confirm = within(dialog).getByRole('button', { name: 'Δέσμευση ασφαλιστηρίου' });
    expect(confirm).toHaveAttribute('aria-disabled', 'true');
    await user.click(
      within(dialog).getByRole('checkbox', { name: 'Επιβεβαιώνω τη δέσμευση της προσφοράς.' }),
    );
    await user.click(within(dialog).getByRole('button', { name: 'Δέσμευση ασφαλιστηρίου' }));

    expect(await screen.findByText('Δεσμεύτηκε το ασφαλιστήριο POL000000007')).toBeInTheDocument();
    const bind = api.callsTo('POST', '/api/pol/v1/jobs/bind')[0];
    expect(bind?.body).toEqual({
      jobId: fx.jobId,
      versionNo: 1,
      paymentPlanOption: 'ANNUAL',
      confirmation: true,
    });
    expect(bind?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    await user.click(screen.getByRole('button', { name: 'Άνοιγμα ασφαλιστηρίου' }));
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/policies\/[0-9a-f-]{36}$/);
  });

  it('shows the gate results when binding does not complete', async () => {
    const failed: JobBindResponse = fx.bound({
      state: 'QUOTED',
      gateResults: [
        {
          gate: 'EFFECTIVE_DATE',
          passed: false,
          severity: 'BLOCK',
          reason: 'POL-ERR-RETROACTIVE-MTPL',
        },
        { gate: 'UW_ISSUES', passed: true, severity: 'BLOCK' },
      ],
    });
    mockApi(routes({ bind: () => ({ body: failed }) }));
    const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
    await walkToPremium(user);
    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    await screen.findAllByText('Αποδοχή');
    await next(user);
    await user.click(await screen.findByRole('button', { name: 'Δέσμευση' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox'));
    await user.click(within(dialog).getByRole('button', { name: 'Δέσμευση ασφαλιστηρίου' }));

    const list = await screen.findByRole('list', { name: 'Αποτελέσματα ελέγχων δέσμευσης' });
    expect(
      within(list)
        .getByText(/Ημερομηνία έναρξης/)
        .closest('li'),
    ).toHaveTextContent('απέτυχε');
    expect(within(list).getByText(/POL-ERR-RETROACTIVE-MTPL/)).toBeInTheDocument();
    expect(
      within(list)
        .getByText(/Ζητήματα ανάληψης κινδύνου/)
        .closest('li'),
    ).toHaveTextContent('επιτυχής');
    expect(screen.queryByText(/Δεσμεύτηκε το ασφαλιστήριο/)).not.toBeInTheDocument();
  });

  it('explains a bind stopped by an underwriting referral: what, why, status and the next step', async () => {
    const referred: JobBindResponse = fx.bound({
      state: 'QUOTED',
      gateResults: [
        { gate: 'EFFECTIVE_DATE', passed: true, severity: 'BLOCK' },
        { gate: 'UW_ISSUES', passed: false, severity: 'BLOCK', reason: 'UW_ISSUES_OPEN' },
      ],
    });
    const issue = {
      id: '0192f0c4-0000-7000-8000-0000000000aa',
      jobRef: fx.jobId,
      issueType: 'VEHICLE_AGE_REFERRAL',
      issueKey: 'VEHICLE_AGE_REFERRAL:veh-1',
      ruleId: 'REFER-OLD-VEHICLE',
      severity: 'REFER',
      blockingPoint: 'PRE_BIND',
      lane: 'ASSISTED',
      status: 'Open',
      recordVersion: 1,
      messageEn: 'The vehicle is older than 20 years.',
      messageEl: 'Το όχημα είναι παλαιότερο των 20 ετών.',
      raisedAt: '2026-10-08T10:00:00Z',
      raisedBy: 'USER:dev:underwriter',
      decision: null,
      closeReason: null,
    };
    const api = mockApi([
      ...routes({ bind: () => ({ body: referred }) }),
      {
        method: 'GET',
        path: '/api/uw/v1/issues',
        respond: () => ({ body: { items: [issue], nextCursor: null, limit: 200 } }),
      },
    ]);
    const { user, container } = renderScreen(<QuoteWizardPage />, {
      path: '/policies/quotes/new',
      url,
    });
    await walkToPremium(user);
    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    await screen.findAllByText('Αποδοχή');
    await next(user);
    await user.click(await screen.findByRole('button', { name: 'Δέσμευση' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox'));
    await user.click(within(dialog).getByRole('button', { name: 'Δέσμευση ασφαλιστηρίου' }));

    const grid = await screen.findByRole('grid', { name: 'Ζητήματα ανάληψης της προσφοράς' });
    expect(within(grid).getByText('Παλαιό όχημα')).toBeInTheDocument();
    expect(
      within(grid).getByText('Το όχημα είναι παλαιότερο των 20 ετών (από το έτος πρώτης άδειας).'),
    ).toBeInTheDocument();
    expect(within(grid).getByText('Αναμένει απόφαση')).toBeInTheDocument();
    expect(within(grid).getByText(/Ζητήστε έγκριση από ανώτερο ανάδοχο/)).toBeInTheDocument();
    expect(within(grid).getByText(/Ελέγξτε το έτος πρώτης άδειας/)).toBeInTheDocument();
    expect(screen.queryByText(/VEHICLE_AGE_REFERRAL:/)).not.toBeInTheDocument();
    expect(screen.queryByText(/\(UW_ISSUES_OPEN\)/)).not.toBeInTheDocument();
    expect(api.callsTo('GET', '/api/uw/v1/issues')[0]?.url.searchParams.get('jobRef')).toBe(
      fx.jobId,
    );
    // The bind can be tried again once a senior underwriter has approved.
    expect(screen.getByRole('button', { name: 'Δέσμευση' })).not.toHaveAttribute(
      'aria-disabled',
      'true',
    );
    await expectNoA11yViolations(container);
  });

  it('explains a stale quote (POL-ERR-QUOTE-STALE) as a Problem Details error', async () => {
    mockApi(routes({ bind: () => problem(409, 'POL-ERR-QUOTE-STALE', 'Η προσφορά έχει λήξει') }));
    const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
    await walkToPremium(user);
    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    await screen.findAllByText('Αποδοχή');
    await next(user);
    await user.click(await screen.findByRole('button', { name: 'Δέσμευση' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox'));
    await user.click(within(dialog).getByRole('button', { name: 'Δέσμευση ασφαλιστηρίου' }));
    expect(await screen.findByText('Η δέσμευση απέτυχε')).toBeInTheDocument();
    expect(screen.getByText(/Υπολογίστε νέα προσφορά πριν τη δέσμευση/)).toBeInTheDocument();
    expect(screen.getByText('POL-ERR-QUOTE-STALE')).toBeInTheDocument();
  });

  it('blocks binding of a referred quote and marks the price stale after an input change', async () => {
    mockApi(
      routes({
        quote: () => ({
          body: fx.quote({
            decision: 'REFER',
            referred: true,
            bindable: false,
            issues: [
              {
                issueId: 'i1',
                issueType: 'REFERRAL',
                blockingPoint: 'PRE_BIND',
                issueKey: 'UW-X',
                approvalStatus: 'OPEN',
              },
            ],
          }),
        }),
      }),
    );
    const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
    await walkToPremium(user);
    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    expect((await screen.findAllByText('Παραπομπή')).length).toBeGreaterThan(0);
    await next(user);
    const commit = await screen.findByRole('button', { name: 'Δέσμευση' });
    expect(commit).toHaveAttribute('aria-disabled', 'true');

    // Going back and changing an input invalidates the calculated price.
    await user.click(screen.getByRole('button', { name: /Πίσω/ }));
    await user.click(screen.getByRole('button', { name: /Πίσω/ }));
    await screen.findByRole('heading', { level: 2, name: 'Ερωτήσεις κινδύνου' });
    await user.click(screen.getByRole('radio', { name: 'Επαγγελματική χρήση' }));
    await user.click(screen.getByRole('button', { name: /Επόμενο/ }));
    expect(await screen.findByText('Τα στοιχεία άλλαξαν μετά τον υπολογισμό')).toBeInTheDocument();
  });

  it('refuses to go on when a question has a knock-out answer', async () => {
    mockApi(routes());
    const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
    await walkToPremium(user);
    await user.click(screen.getByRole('button', { name: /Πίσω/ }));
    await user.click(screen.getByRole('radio', { name: /^Ναι/ }));
    expect(await screen.findByText('Η απάντηση αποκλείει την ασφάλιση')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Επόμενο/ })).toHaveAttribute(
      'aria-disabled',
      'true',
    );
  });

  describe('vehicle value for Own damage (D-SLC-21)', () => {
    const missing = 'Η αξία του οχήματος απαιτείται για την κάλυψη Ίδιες ζημιές.';

    async function selectOwnDamage(user: User) {
      await walkToCovers(user);
      await chooseOption(user, /Σωματικές βλάβες ανά άτομο/, '1.300.000 €');
      await user.click(screen.getByRole('checkbox', { name: /Ίδιες ζημιές/ }));
      await user.type(screen.getByRole('textbox', { name: /Ασφαλιζόμενο ποσό/ }), '9000');
      await chooseOption(user, /Απαλλαγή/, 'Χωρίς απαλλαγή');
    }

    it('does not leave the covers step without a vehicle value and says why', async () => {
      mockApi(routes());
      const { user, container } = renderScreen(<QuoteWizardPage />, {
        path: '/policies/quotes/new',
        url,
      });
      await selectOwnDamage(user);

      expect(await screen.findByText(missing)).toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Επόμενο/ })).toHaveAttribute(
        'aria-disabled',
        'true',
      );
      await expectNoA11yViolations(container);

      // Entering the value on the covers step lets the user go on.
      await user.type(screen.getByRole('textbox', { name: /Αξία οχήματος/ }), '12000');
      await user.tab();
      await waitFor(() => {
        expect(screen.queryByText(missing)).not.toBeInTheDocument();
      });
      expect(screen.getByRole('button', { name: /Επόμενο/ })).not.toHaveAttribute(
        'aria-disabled',
        'true',
      );
    });

    it('does not require the value when Own damage is not selected', async () => {
      mockApi(routes());
      const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
      await walkToCovers(user);
      await chooseOption(user, /Σωματικές βλάβες ανά άτομο/, '1.300.000 €');
      expect(screen.queryByText(missing)).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: /Επόμενο/ })).not.toHaveAttribute(
        'aria-disabled',
        'true',
      );
    });
  });

  describe('rating input errors in plain words', () => {
    const cases: {
      name: string;
      detail: string;
      what: RegExp;
      step: string;
      focus?: RegExp;
    }[] = [
      {
        name: 'vehicle.vehicleValue',
        detail: 'Rating failed: RAT-ERR-INPUT vehicle.vehicleValue is required to rate OWN-DAMAGE.',
        what: /Λείπει η αξία του οχήματος, οπότε δεν μπορεί να τιμολογηθεί η κάλυψη «Ίδιες ζημιές»/,
        step: 'Όχημα',
        focus: /Αξία οχήματος/,
      },
      {
        name: 'vehicle.firstRegistrationYear',
        detail:
          'Rating failed: RAT-ERR-INPUT segments[a].riskTree.vehicle.firstRegistrationYear: A whole number from 1950 to 2100 is expected.',
        what: /Το έτος πρώτης κυκλοφορίας λείπει ή δεν είναι έγκυρο/,
        step: 'Όχημα',
        focus: /Έτος πρώτης κυκλοφορίας/,
      },
      {
        name: 'vehicle.engineCapacityCc',
        detail:
          'Rating failed: RAT-ERR-INPUT segments[a].riskTree.vehicle.engineCapacityCc: A whole number from 1 to 20000 is expected.',
        what: /Ο κυβισμός λείπει ή δεν είναι έγκυρος/,
        step: 'Όχημα',
        focus: /Κυβισμός/,
      },
      {
        name: 'vehicle.usage',
        detail: 'Rating failed: RAT-ERR-INPUT segments[a].riskTree.vehicle.usage: bad.',
        what: /Η χρήση του οχήματος δεν είναι έγκυρη/,
        step: 'Ερωτήσεις κινδύνου',
      },
      {
        name: 'driver.dateOfBirth',
        detail:
          'Rating failed: RAT-ERR-INPUT segments[a].riskTree.driver.dateOfBirth: The value is required.',
        what: /Ο οδηγός δεν έχει ημερομηνία γέννησης/,
        step: 'Οδηγός',
      },
      {
        name: 'driver.licenceIssueDate',
        detail:
          'Rating failed: RAT-ERR-INPUT segments[a].riskTree.driver.licenceIssueDate: A date is expected.',
        what: /Η ημερομηνία απόκτησης διπλώματος λείπει ή δεν είναι έγκυρη/,
        step: 'Οδηγός',
        focus: /Έτος απόκτησης διπλώματος/,
      },
      {
        name: 'driver.claimsLast5Years',
        detail:
          'Rating failed: RAT-ERR-INPUT segments[a].riskTree.driver.claimsLast5Years: A whole number from 0 to 50 is expected.',
        what: /Ο αριθμός ζημιών δεν είναι έγκυρος/,
        step: 'Ερωτήσεις κινδύνου',
      },
      {
        name: 'coverages',
        detail:
          'Rating failed: RAT-ERR-INPUT segments[a].riskTree.coverages: Select at least one coverage.',
        what: /Δεν έχει επιλεγεί καμία κάλυψη/,
        step: 'Καλύψεις',
      },
      {
        name: 'effectiveDate',
        detail: 'Rating failed: RAT-ERR-INPUT effectiveDate: not in the validity of the artefact.',
        what: /Η ημερομηνία έναρξης δεν είναι έγκυρη για την τιμολόγηση/,
        step: 'Λήπτης και προϊόν',
      },
    ];

    it.each(cases)(
      'explains $name and the Go to button jumps to the step',
      async ({ detail, what, step, focus }) => {
        mockApi(
          routes({ quote: () => problem(422, 'POL-ERR-RATING', 'Η τιμολόγηση απέτυχε', detail) }),
        );
        const { user, container } = renderScreen(<QuoteWizardPage />, {
          path: '/policies/quotes/new',
          url,
        });
        await walkToPremium(user);
        await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));

        expect(await screen.findByText(what)).toBeInTheDocument();
        // The code, trace id and the engine's own sentence are secondary, under "Technical details".
        expect(screen.getByText('Τεχνικές λεπτομέρειες')).toBeInTheDocument();
        expect(screen.getByText(/trace-0123456789abcdef/)).toBeInTheDocument();
        await expectNoA11yViolations(container);

        await user.click(screen.getByRole('button', { name: `Μετάβαση στο βήμα «${step}»` }));
        await screen.findByRole('heading', { level: 2, name: step });
        expect(screen.queryByText(what)).not.toBeInTheDocument();
        if (focus) {
          await waitFor(() => {
            expect(screen.getByRole('textbox', { name: focus })).toHaveFocus();
          });
        }
      },
    );

    it('falls back to a generic explanation for an input path it does not know', async () => {
      mockApi(
        routes({
          quote: () =>
            problem(
              422,
              'RAT-ERR-INPUT',
              'Τα στοιχεία κινδύνου δεν είναι έγκυρα',
              'something: odd',
            ),
        }),
      );
      const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
      await walkToPremium(user);
      await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
      expect(
        await screen.findByText(/Η μηχανή τιμολόγησης απέρριψε ένα από τα στοιχεία/),
      ).toBeInTheDocument();
    });
  });
});
