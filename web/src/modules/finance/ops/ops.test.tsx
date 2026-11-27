import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, problem, renderScreen } from '../../../test/mockApi';
import { FinanceHomePage } from '../FinanceHomePage';
import { EarningRunPage } from './EarningRunPage';
import { IntakeExceptionsPage } from './IntakeExceptionsPage';
import { ManualJournalFormPage } from './ManualJournalFormPage';
import { ManualJournalPage } from './ManualJournalPage';
import { PeriodClosePage } from './PeriodClosePage';
import { TrialBalancePage } from './TrialBalancePage';
import type { ManualJournal } from './types';

vi.setConfig({ testTimeout: 60_000 });

function signIn(id: string, roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id, name: id, roles },
    }),
  );
}

beforeEach(() => {
  signIn('fin-maker', ['Staff.Finance']);
});
afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

const journalId = 'a0000000-1111-4222-8333-000000000010';
const journal = (over: Partial<ManualJournal> = {}): ManualJournal => ({
  id: journalId,
  number: 'MJ000001',
  status: 'SUBMITTED',
  description: 'Διόρθωση προμηθειών',
  book: 'IFRS',
  accountingDate: '2026-10-15',
  currency: 'EUR',
  lines: [
    { account: '6100', side: 'DEBIT', amount: '120.00' },
    { account: '2400', side: 'CREDIT', amount: '120.00' },
  ],
  evidenceRef: 'DOC-77',
  submittedBy: 'fin-maker',
  ...over,
});

function nth(list: HTMLElement[], index: number): HTMLElement {
  const element = list[index];
  if (!element) throw new Error(`No element at ${String(index)}`);
  return element;
}

describe('TrialBalancePage', () => {
  it('shows per-account debit and credit with balanced totals', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/balances/trial-balance',
        respond: () => ({
          body: {
            period: '2026-10',
            book: null,
            currency: 'EUR',
            balances: [
              {
                book: 'IFRS',
                account: '1100',
                nameEl: 'Εισπρακτέα',
                nameEn: 'Receivables',
                debit: '479.20',
                credit: '0.00',
                balance: '479.20',
              },
              {
                book: 'IFRS',
                account: '4000',
                nameEl: 'Ασφάλιστρα',
                nameEn: 'Premiums',
                debit: '0.00',
                credit: '479.20',
                balance: '-479.20',
              },
            ],
            totalDebit: '479.20',
            totalCredit: '479.20',
          },
        }),
      },
    ]);
    const { container } = renderScreen(<TrialBalancePage />, {
      path: '/finance/trial-balance',
      url: '/finance/trial-balance',
    });
    const grid = await screen.findByRole('grid', { name: /Ισοζύγιο 20\d\d-\d\d/ });
    expect(within(grid).getByText('1100 · Εισπρακτέα')).toBeInTheDocument();
    expect(await screen.findByText('Οι χρεώσεις ισούνται με τις πιστώσεις')).toBeInTheDocument();
    expect(api.calls[0]?.url.searchParams.get('period')).toMatch(/^\d{4}-\d{2}$/);
    await expectNoA11yViolations(container);
  });

  it('flags an unbalanced trial balance', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/balances/trial-balance',
        respond: () => ({
          body: {
            period: '2026-10',
            book: null,
            currency: 'EUR',
            balances: [
              {
                book: 'IFRS',
                account: '1100',
                nameEl: 'Εισπρακτέα',
                nameEn: 'Receivables',
                debit: '10.00',
                credit: '0.00',
                balance: '10.00',
              },
            ],
            totalDebit: '10.00',
            totalCredit: '0.00',
          },
        }),
      },
    ]);
    renderScreen(<TrialBalancePage />, {
      path: '/finance/trial-balance',
      url: '/finance/trial-balance',
    });
    expect(
      await screen.findByText('Οι χρεώσεις δεν ισούνται με τις πιστώσεις'),
    ).toBeInTheDocument();
  });
});

describe('ManualJournalFormPage', () => {
  it('stops an unbalanced journal client-side and submits a balanced one with an idempotency key', async () => {
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/fin/v1/manual-journals',
        respond: () => ({ body: { manualJournal: journal() } }),
      },
      {
        method: 'GET',
        path: `/api/fin/v1/manual-journals/${journalId}`,
        respond: () => ({ body: { manualJournal: journal() } }),
      },
    ]);
    const { user } = renderScreen(<ManualJournalFormPage />, {
      path: '/finance/manual-journals/new',
      url: '/finance/manual-journals/new',
    });
    await user.type(screen.getByRole('textbox', { name: /Περιγραφή/ }), 'Διόρθωση προμηθειών');
    await user.type(screen.getByRole('textbox', { name: /Βιβλίο/ }), 'IFRS');
    await user.type(screen.getByRole('textbox', { name: /Παραπομπή τεκμηρίου/ }), 'DOC-77');
    const accounts = screen.getAllByRole('textbox', { name: /Λογαριασμός/ });
    await user.type(nth(accounts, 0), '6100');
    await user.type(nth(accounts, 1), '2400');
    const amounts = screen.getAllByRole('textbox', { name: /Ποσό/ });
    await user.type(nth(amounts, 0), '120');
    await user.type(nth(amounts, 1), '100');
    await user.click(screen.getByRole('button', { name: 'Αποθήκευση πρόχειρου' }));
    expect((await screen.findAllByText(/πρέπει να είναι ίσες/)).length).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/fin/v1/manual-journals')).toHaveLength(0);

    await user.clear(nth(amounts, 1));
    await user.type(nth(amounts, 1), '120');
    await user.click(screen.getByRole('button', { name: 'Αποθήκευση πρόχειρου' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/fin/v1/manual-journals')).toHaveLength(1);
    });
    const call = api.callsTo('POST', '/api/fin/v1/manual-journals')[0];
    expect(call?.headers.get('Idempotency-Key')).toBeTruthy();
    expect(call?.body).toMatchObject({
      book: 'IFRS',
      evidence: { reference: 'DOC-77' },
      lines: [
        { account: '6100', side: 'DEBIT', amount: '120.00' },
        { account: '2400', side: 'CREDIT', amount: '120.00' },
      ],
    });
  });
});

describe('ManualJournalPage four-eyes', () => {
  const page = () =>
    renderScreen(<ManualJournalPage />, {
      path: '/finance/manual-journals/:id',
      url: `/finance/manual-journals/${journalId}`,
    });
  const getRoute = (j: ManualJournal) => ({
    method: 'GET',
    path: `/api/fin/v1/manual-journals/${journalId}`,
    respond: () => ({ body: { manualJournal: j } }),
  });

  it('keeps the maker from deciding their own journal and offers withdrawal', async () => {
    mockApi([getRoute(journal())]);
    page();
    expect(await screen.findByText('Απαιτείται δεύτερος χρήστης')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Έγκριση και καταχώριση' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Απόρριψη' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Απόσυρση' })).toBeEnabled();
  });

  it('shows the server separation-of-duties refusal with its explanation', async () => {
    signIn('fin-checker', ['Staff.Finance']);
    mockApi([
      getRoute(journal()),
      {
        method: 'POST',
        path: '/api/fin/v1/manual-journals/decide',
        respond: () => problem(403, 'PLT-ERR-SOD', 'Forbidden'),
      },
    ]);
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: 'Έγκριση και καταχώριση' }));
    expect(
      await screen.findByText(/Διαχωρισμός καθηκόντων: όποιος ετοίμασε την εγγραφή/),
    ).toBeInTheDocument();
    expect(screen.getByText('PLT-ERR-SOD')).toBeInTheDocument();
  });

  it('requires a reason to reject and sends the decision', async () => {
    signIn('fin-checker', ['Staff.Finance']);
    const api = mockApi([
      getRoute(journal()),
      {
        method: 'POST',
        path: '/api/fin/v1/manual-journals/decide',
        respond: () => ({ body: { manualJournal: journal({ status: 'REJECTED' }) } }),
      },
    ]);
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: 'Απόρριψη' }));
    expect(await screen.findByText('Δώστε αιτιολογία απόρριψης.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/fin/v1/manual-journals/decide')).toHaveLength(0);
    await user.type(screen.getByRole('textbox', { name: /Αιτιολογία/ }), 'Λάθος λογαριασμός');
    await user.click(screen.getByRole('button', { name: 'Απόρριψη' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/fin/v1/manual-journals/decide')[0]?.body).toEqual({
        id: journalId,
        decision: 'REJECT',
        reason: 'Λάθος λογαριασμός',
      });
    });
  });
});

describe('IntakeExceptionsPage', () => {
  it('lists suspended events and reruns the selected one', async () => {
    const eventId = 'e0000000-1111-4222-8333-000000000001';
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/intake-exceptions',
        respond: () => ({
          body: {
            items: [
              {
                eventId,
                sourceModule: 'BIL',
                eventType: 'BillingEntryPosted',
                aggregateType: 'BillingEntry',
                aggregateId: 'x1',
                reason: 'NO_RULE',
                detail: 'Δεν βρέθηκε κανόνας',
                attempts: 2,
                receivedAt: '2026-10-20T10:00:00Z',
                updatedAt: '2026-10-20T10:00:00Z',
              },
            ],
            nextCursor: null,
            limit: 100,
          },
        }),
      },
      {
        method: 'POST',
        path: '/api/fin/v1/intake-exceptions/rerun',
        respond: () => ({ body: {} }),
      },
    ]);
    const { user } = renderScreen(<IntakeExceptionsPage />, {
      path: '/finance/intake-exceptions',
      url: '/finance/intake-exceptions',
    });
    const grid = await screen.findByRole('grid', { name: 'Γεγονότα σε αναστολή' });
    await user.dblClick(within(grid).getByText('BillingEntryPosted'));
    await user.click(await screen.findByRole('button', { name: 'Εκτέλεση ξανά' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/fin/v1/intake-exceptions/rerun')[0]?.body).toEqual({
        itemIds: [eventId],
      });
    });
  });

  it('shows the empty state', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/intake-exceptions',
        respond: () => ({ body: { items: [], nextCursor: null, limit: 100 } }),
      },
    ]);
    renderScreen(<IntakeExceptionsPage />, {
      path: '/finance/intake-exceptions',
      url: '/finance/intake-exceptions',
    });
    expect(await screen.findByText('Δεν υπάρχουν εξαιρέσεις εισαγωγής')).toBeInTheDocument();
  });
});

describe('PeriodClosePage', () => {
  it('lists periods with their state and a missing-permission error is explained', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/periods',
        respond: () => ({
          body: {
            items: [
              { period: '2026-10', state: 'OPEN', closedForPosting: false },
              { period: '2026-09', state: 'LOCKED', closedForPosting: true },
            ],
            nextCursor: null,
            limit: 36,
          },
        }),
      },
    ]);
    const { container } = renderScreen(<PeriodClosePage />, {
      path: '/finance/period-close',
      url: '/finance/period-close',
    });
    const grid = await screen.findByRole('grid', { name: 'Λογιστικές περίοδοι' });
    expect(within(grid).getByText('2026-09')).toBeInTheDocument();
    expect(within(grid).getByText('Κλειδωμένη')).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('shows the checklist of a period and offers sign-off while it is open', async () => {
    const signed: unknown[] = [];
    mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/periods',
        respond: () => ({
          body: {
            items: [{ period: '2026-10', state: 'OPEN', closedForPosting: false }],
            nextCursor: null,
          },
        }),
      },
      {
        method: 'GET',
        path: '/api/fin/v1/close/tasks',
        respond: () => ({
          body: {
            period: '2026-10',
            state: 'OPEN',
            closedForPosting: false,
            tasks: [
              {
                task: 'INTAKE_EXCEPTIONS',
                status: 'OPEN',
                signOffRole: 'Staff.Finance',
                blocker: null,
              },
              {
                task: 'RECONCILIATIONS',
                status: 'SIGNED_OFF',
                signOffRole: 'Staff.Finance',
                signedOffBy: 'ann',
              },
            ],
          },
        }),
      },
      {
        method: 'POST',
        path: '/api/fin/v1/close/sign-off',
        respond: ({ body }) => {
          signed.push(body);
          return { body: {} };
        },
      },
    ]);
    const { user } = renderScreen(<PeriodClosePage />, {
      path: '/finance/period-close',
      url: '/finance/period-close',
    });
    await user.dblClick(await screen.findByText('2026-10'));
    await user.click(await screen.findByRole('button', { name: 'Υπογραφή' }));
    await waitFor(() => {
      expect(signed).toEqual([{ period: '2026-10', task: 'INTAKE_EXCEPTIONS' }]);
    });
  });

  it('shows the no-permission banner on a 403', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/periods',
        respond: () => ({ status: 403, body: { status: 403, title: 'Forbidden' } }),
      },
    ]);
    renderScreen(<PeriodClosePage />, {
      path: '/finance/period-close',
      url: '/finance/period-close',
    });
    expect(await screen.findByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });
});

describe('EarningRunPage', () => {
  it('shows an unserved status as an error and asks before running', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/earning/status',
        respond: () => ({ body: { run: null } }),
      },
    ]);
    const { user } = renderScreen(<EarningRunPage />, {
      path: '/finance/earning',
      url: '/finance/earning',
    });
    expect(
      await screen.findByText('Δεν έχει γίνει ακόμη εκτέλεση κατάκτησης.'),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Εκτέλεση κατάκτησης' }));
    expect(await screen.findByRole('dialog')).toBeInTheDocument();
  });
});

describe('FinanceHomePage', () => {
  it('shows the operations screens and the periods to finance roles', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/fin/v1/periods',
        respond: () => ({
          body: {
            items: [{ period: '2026-10', state: 'OPEN', closedForPosting: false }],
            nextCursor: null,
          },
        }),
      },
    ]);
    renderScreen(<FinanceHomePage />, { path: '/finance', url: '/finance' });
    expect(await screen.findByRole('button', { name: 'Ισοζύγιο' })).toBeInTheDocument();
    expect(await screen.findByText('2026-10')).toBeInTheDocument();
  });

  it('keeps the operations screens from billing users', () => {
    signIn('billing', ['Staff.Billing']);
    renderScreen(<FinanceHomePage />, { path: '/finance', url: '/finance' });
    expect(screen.queryByRole('button', { name: 'Ισοζύγιο' })).not.toBeInTheDocument();
  });
});
