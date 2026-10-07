import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import * as fx from '../../test/fixtures';
import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { FinanceHomePage } from './FinanceHomePage';
import { journalTotals } from './journal';
import { JournalPage } from './JournalPage';

vi.setConfig({ testTimeout: 30_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const route = (reply: () => { status?: number; body?: unknown }) => ({
  method: 'GET',
  path: '/api/fin/v1/journals/query',
  respond: reply,
});
const page = () => renderScreen(<JournalPage />, { path: '/finance/journals/policy/:policyNumber', url: '/finance/journals/policy/POL000000007' });

describe('journalTotals', () => {
  it('sums exactly in minor units and detects imbalance', () => {
    const eur = (amount: string) => ({ amount, currency: 'EUR' });
    expect(journalTotals([{ side: 'DEBIT', amount: eur('0.10') }, { side: 'DEBIT', amount: eur('0.20') }, { side: 'CREDIT', amount: eur('0.30') }])).toEqual({ debit: '0.30', credit: '0.30', balanced: true });
    expect(journalTotals([{ side: 'DEBIT', amount: eur('10.00') }, { side: 'CREDIT', amount: eur('9.99') }]).balanced).toBe(false);
  });
});

describe('JournalPage', () => {
  it('lists journal entries for the policy with debit/credit lines and a balanced marker (read-only)', async () => {
    const api = mockApi([route(() => ({ body: fx.journals() }))]);
    const { container } = page();
    expect(await screen.findByRole('heading', { level: 1, name: 'Λογιστικές εγγραφές ασφαλιστηρίου POL000000007' })).toBeInTheDocument();
    const lines = await screen.findByRole('grid', { name: 'Γραμμές της εγγραφής J000000001' });
    expect(within(lines).getByText('1100 · Λογαριασμός 1100')).toBeInTheDocument();
    expect(within(lines).getAllByText('479,20 €')).toHaveLength(1);
    expect(within(lines).getByText('432,35 €')).toBeInTheDocument();
    expect(screen.getByText('Ισοσκελισμένη')).toBeInTheDocument();
    expect(screen.getByText(/Χρέωση 479,20 € · Πίστωση 479,20 €/)).toBeInTheDocument();
    expect(screen.getByText('Μόνο ανάγνωση')).toBeInTheDocument();
    // Nothing but the policy number (a business key) in the query.
    expect(api.calls[0]?.url.search).toBe('?policyNumber=POL000000007&limit=50');
    expect(screen.queryByRole('button', { name: /Αποθήκευση|Καταχώριση/ })).not.toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('flags an unbalanced entry', async () => {
    mockApi([route(() => ({ body: fx.journals(true) }))]);
    page();
    expect(await screen.findByText('Μη ισοσκελισμένη')).toBeInTheDocument();
  });

  it('shows the empty state, the error state with retry, and English', async () => {
    let calls = 0;
    mockApi([route(() => (++calls === 1 ? problem(500, 'FIN-ERR-INTERNAL', 'Σφάλμα') : { body: { items: [], nextCursor: null, limit: 50 } }))]);
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: /Επανάληψη/ }));
    expect(await screen.findByText('Δεν υπάρχουν λογιστικές εγγραφές')).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    mockApi([route(() => ({ body: fx.journals() }))]);
    page();
    expect(await screen.findByRole('heading', { level: 1, name: 'Journal entries of policy POL000000007' })).toBeInTheDocument();
    expect(await screen.findByText('Balanced')).toBeInTheDocument();
  });
});

describe('FinanceHomePage', () => {
  it('looks a policy up by number and validates it', async () => {
    const { user, container } = renderScreen(<FinanceHomePage />, { path: '/finance', url: '/finance' });
    await expectNoA11yViolations(container);
    await user.type(screen.getByRole('textbox', { name: 'Αριθμός ασφαλιστηρίου' }), '!{Enter}');
    expect(await screen.findByText('Δώστε έγκυρο αριθμό ασφαλιστηρίου.')).toBeInTheDocument();
    await user.clear(screen.getByRole('textbox', { name: 'Αριθμός ασφαλιστηρίου' }));
    await user.type(screen.getByRole('textbox', { name: 'Αριθμός ασφαλιστηρίου' }), 'POL000000007{Enter}');
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent('/finance/journals/policy/POL000000007');
    });
  });
});
