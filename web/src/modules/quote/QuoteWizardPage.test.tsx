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

function routes(overrides: { bind?: () => { status?: number; body?: unknown }; quote?: () => { status?: number; body?: unknown } } = {}): MockRoute[] {
  return [
    { method: 'GET', path: `/api/pty/v1/parties/${fx.partyId}`, respond: () => ({ body: { party: fx.party() } }) },
    { method: 'POST', path: '/api/pfc/v1/product-versions/resolve', respond: () => ({ body: fx.resolved }) },
    { method: 'GET', path: `/api/pfc/v1/catalogue/${fx.hash}`, respond: () => ({ body: fx.catalogue }) },
    { method: 'GET', path: `/api/pfc/v1/question-sets/${fx.hash}`, respond: () => ({ body: fx.questionSet }) },
    {
      method: 'POST',
      path: '/api/pfc/v1/question-sets/evaluate',
      respond: () => ({ body: { questions: [], knockOuts: [], referrals: [], missingRequired: [], complete: true } }),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/submissions',
      respond: () => ({ status: 201, body: { jobId: fx.jobId, jobNumber: 'Q1', policyId: fx.policyId, state: 'DRAFT', productVersion: '1.0', expirationAt: '2027-10-08T00:00:00Z', versionNo: 1, manifest: { artefactHash: fx.hash } } }),
    },
    {
      method: 'POST',
      path: '/api/pol/v1/jobs/update-draft',
      respond: (r) => {
        const body = r.body as { versionNo: number; expectedDraftVersion: number; instructions: { op: string }[] };
        const hasVehicle = body.instructions.some((i) => i.op === 'SET_VEHICLE');
        return {
          body: {
            jobId: fx.jobId,
            versionNo: body.versionNo,
            draftVersion: body.expectedDraftVersion + 1,
            state: 'DRAFT',
            riskTree: {
              vehicles: [{ locator: 'veh-1', plate: 'ΙΚΧ1234' }],
              drivers: hasVehicle ? [] : [{ locator: 'drv-1', partyId: fx.partyId, driverType: 'MAIN' }],
              coverages: [],
              questionSets: [],
            },
            validation: [],
          },
        };
      },
    },
    { method: 'POST', path: '/api/pol/v1/jobs/quote', respond: overrides.quote ?? (() => ({ body: fx.quote() })) },
    { method: 'POST', path: '/api/pol/v1/jobs/bind', respond: overrides.bind ?? (() => ({ body: fx.bound() })) },
  ];
}

const next = (user: User) => user.click(screen.getByRole('button', { name: /Επόμενο/ }));

async function chooseOption(user: User, label: RegExp, option: string) {
  await user.click(screen.getByRole('button', { name: label }));
  await user.click(await screen.findByRole('option', { name: option }));
}

/** Walks policyholder → vehicle → driver → covers → questions and lands on the premium step. */
async function walkToPremium(user: User) {
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
  await user.type(screen.getByRole('textbox', { name: /Ταχυδρομικός κώδικας στάθμευσης/ }), '11526');
  await next(user);

  await screen.findByRole('heading', { level: 2, name: 'Οδηγός' });
  await user.type(screen.getByRole('textbox', { name: /Έτος απόκτησης διπλώματος/ }), '2010');
  await next(user);

  await screen.findByRole('heading', { level: 2, name: 'Καλύψεις' });
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
    const { user, container } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });

    await walkToPremium(user);
    await expectNoA11yViolations(container);
    // Not yet calculated: Next explains why.
    expect(screen.getByRole('button', { name: /Επόμενο/ })).toHaveAttribute('aria-disabled', 'true');

    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    expect(await screen.findByText('Ενδεικτικό τιμολόγιο')).toBeInTheDocument();
    expect(screen.getByText('Προσωρινοί φόροι και εισφορές')).toBeInTheDocument();
    expect(screen.getByText('Αποδοχή')).toBeInTheDocument();
    expect(screen.getAllByText('479,20 €').length).toBeGreaterThan(0);

    // The commands: one submission, two draft writes (vehicle first, then driver/covers/answers), one quote.
    const submission = api.callsTo('POST', '/api/pol/v1/submissions')[0];
    expect(submission?.body).toMatchObject({ policyholderPartyId: fx.partyId, product: 'MOTOR-GR', channel: 'STAFF', quoteType: 'FULL' });
    const drafts = api.callsTo('POST', '/api/pol/v1/jobs/update-draft');
    expect(drafts).toHaveLength(2);
    const [vehicleCall, riskCall] = drafts.map((d) => d.body as { expectedDraftVersion: number; instructions: { op: string; driver?: { vehicleLocator: string } }[] });
    expect(vehicleCall?.instructions.map((i) => i.op)).toEqual(['SET_VEHICLE']);
    expect(riskCall?.expectedDraftVersion).toBe(1);
    expect(riskCall?.instructions.map((i) => i.op)).toEqual(['SET_DRIVER', 'SET_COVERAGES', 'SET_ANSWERS']);
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
    await user.click(within(dialog).getByRole('checkbox', { name: 'Επιβεβαιώνω τη δέσμευση της προσφοράς.' }));
    await user.click(confirm);

    expect(await screen.findByText('Δεσμεύτηκε το ασφαλιστήριο POL000000007')).toBeInTheDocument();
    const bind = api.callsTo('POST', '/api/pol/v1/jobs/bind')[0];
    expect(bind?.body).toEqual({ jobId: fx.jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
    expect(bind?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(screen.getByRole('button', { name: 'Άνοιγμα ασφαλιστηρίου' })).toBeInTheDocument();
  });

  it('shows the gate results when binding does not complete', async () => {
    const failed: JobBindResponse = fx.bound({
      policyId: undefined,
      policyNumber: undefined,
      state: 'QUOTED',
      gateResults: [
        { gate: 'EFFECTIVE_DATE', passed: false, severity: 'BLOCK', reason: 'POL-ERR-RETROACTIVE-MTPL' },
        { gate: 'UW_ISSUES', passed: true, severity: 'BLOCK' },
      ],
    });
    mockApi(routes({ bind: () => ({ body: failed }) }));
    const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
    await walkToPremium(user);
    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    await screen.findByText('Αποδοχή');
    await next(user);
    await user.click(await screen.findByRole('button', { name: 'Δέσμευση' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('checkbox'));
    await user.click(within(dialog).getByRole('button', { name: 'Δέσμευση ασφαλιστηρίου' }));

    const list = await screen.findByRole('list', { name: 'Αποτελέσματα ελέγχων δέσμευσης' });
    expect(within(list).getByText(/Ημερομηνία έναρξης/)).toHaveTextContent('απέτυχε');
    expect(within(list).getByText(/POL-ERR-RETROACTIVE-MTPL/)).toBeInTheDocument();
    expect(within(list).getByText(/Ζητήματα ανάληψης κινδύνου/)).toHaveTextContent('επιτυχής');
    expect(screen.queryByText(/Δεσμεύτηκε το ασφαλιστήριο/)).not.toBeInTheDocument();
  });

  it('explains a stale quote (POL-ERR-QUOTE-STALE) as a Problem Details error', async () => {
    mockApi(routes({ bind: () => problem(409, 'POL-ERR-QUOTE-STALE', 'Η προσφορά έχει λήξει') }));
    const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
    await walkToPremium(user);
    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    await screen.findByText('Αποδοχή');
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
            issues: [{ issueId: 'i1', issueType: 'REFERRAL', blockingPoint: 'PRE_BIND', issueKey: 'UW-X', approvalStatus: 'OPEN' }],
          }),
        }),
      }),
    );
    const { user } = renderScreen(<QuoteWizardPage />, { path: '/policies/quotes/new', url });
    await walkToPremium(user);
    await user.click(screen.getByRole('button', { name: 'Υπολογισμός ασφαλίστρου' }));
    expect(await screen.findByText('Παραπομπή')).toBeInTheDocument();
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
    expect(screen.getByRole('button', { name: /Επόμενο/ })).toHaveAttribute('aria-disabled', 'true');
  });
});
