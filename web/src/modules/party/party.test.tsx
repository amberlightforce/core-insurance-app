import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import * as fx from '../../test/fixtures';
import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { PartyCreatePage } from './PartyCreatePage';
import { PartySearchPage } from './PartySearchPage';
import { PartyViewPage } from './PartyViewPage';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PartySearchPage', () => {
  const search = (reply: () => { status?: number; body?: unknown }) => ({
    method: 'POST',
    path: '/api/pty/v1/parties/search',
    respond: reply,
  });

  it('starts with a first-use hint, posts the single search box in the body and lists masked results', async () => {
    const api = mockApi([search(() => ({ body: { items: [fx.searchItem], nextCursor: null, limit: 25 } }))]);
    const { user, container } = renderScreen(<PartySearchPage />, { path: '/parties', url: '/parties' });
    expect(screen.getByRole('heading', { name: 'Αναζητήστε έναν πελάτη' })).toBeInTheDocument();
    await expectNoA11yViolations(container);

    await user.type(screen.getByRole('searchbox', { name: 'Αναζήτηση πελάτη' }), 'Διεπαφής{Enter}');
    const grid = await screen.findByRole('grid', { name: 'Αποτελέσματα αναζήτησης πελατών' });
    expect(within(grid).getByText('Δοκιμή Διεπαφής')).toBeInTheDocument();
    expect(within(grid).getByText('P000000021')).toBeInTheDocument();
    expect(within(grid).getByText('AFM ******201')).toBeInTheDocument();

    const call = api.callsTo('POST', '/api/pty/v1/parties/search')[0];
    expect(call?.body).toEqual({ criteria: 'Διεπαφής' });
    // D-SLC-05: nothing personal in the URL, neither on the request nor in the page route.
    expect(call?.url.search).toBe('?limit=25');
    expect(screen.getByTestId('location')).toHaveTextContent(/^\/parties$/);
    await expectNoA11yViolations(container);
  });

  it('asks for at least two characters before calling the API', async () => {
    const api = mockApi([]);
    const { user } = renderScreen(<PartySearchPage />, { path: '/parties', url: '/parties' });
    await user.type(screen.getByRole('searchbox', { name: 'Αναζήτηση πελάτη' }), 'a{Enter}');
    expect(await screen.findByRole('alert')).toHaveTextContent('τουλάχιστον 2 χαρακτήρες');
    expect(api.calls).toHaveLength(0);
  });

  it('shows an empty-result state with the create action, and Problem Details on failure', async () => {
    let fail = false;
    mockApi([
      search(() =>
        fail ? problem(422, 'PTY-ERR-QUERY-TOO-SHORT', 'Η αναζήτηση είναι πολύ σύντομη') : { body: { items: [], nextCursor: null, limit: 25 } },
      ),
    ]);
    const { user } = renderScreen(<PartySearchPage />, { path: '/parties', url: '/parties' });
    const box = screen.getByRole('searchbox', { name: 'Αναζήτηση πελάτη' });
    await user.type(box, 'zzzz{Enter}');
    expect(await screen.findByRole('heading', { name: 'Δεν βρέθηκαν πελάτες' })).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Νέο πρόσωπο' }).length).toBeGreaterThan(0);

    fail = true;
    await user.type(box, '{Enter}');
    const banner = await screen.findByText('Η αναζήτηση απέτυχε');
    expect(banner).toBeInTheDocument();
    expect(screen.getByText('PTY-ERR-QUERY-TOO-SHORT')).toBeInTheDocument();
    expect(screen.getByText(/trace-0123456789abcdef/)).toBeInTheDocument();
  });

  it('opens a result with Enter and renders in English', async () => {
    mockApi([search(() => ({ body: { items: [fx.searchItem], nextCursor: null, limit: 25 } }))]);
    await i18n.changeLanguage('en');
    const { user } = renderScreen(<PartySearchPage />, { path: '/parties', url: '/parties' });
    await user.type(screen.getByRole('searchbox', { name: 'Search customers' }), 'Diepafis{Enter}');
    const grid = await screen.findByRole('grid', { name: 'Customer search results' });
    const row = within(grid).getAllByRole('row')[1];
    row?.focus();
    await user.keyboard('{Enter}');
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(`/parties/${fx.partyId}`);
    });
  });
});

describe('PartyCreatePage', () => {
  async function fillValid(user: ReturnType<typeof renderScreen>['user'], afm = '094014201') {
    await user.type(screen.getByRole('textbox', { name: /^Όνομα(?! πατέρα)/ }), 'Δοκιμή');
    await user.type(screen.getByRole('textbox', { name: /Επώνυμο/ }), 'Διεπαφής');
    const birth = screen.getByRole('group', { name: /Ημερομηνία γέννησης/ });
    await user.click(within(birth).getAllByRole('spinbutton')[0] as HTMLElement);
    await user.keyboard('12031985');
    await user.type(screen.getByRole('textbox', { name: /ΑΦΜ/ }), afm);
    await user.type(screen.getByRole('textbox', { name: /Οδός/ }), 'Λεωφ. Κηφισίας');
    await user.type(screen.getByRole('textbox', { name: /Ταχυδρομικός κώδικας/ }), '11526');
    await user.type(screen.getByRole('textbox', { name: /Πόλη/ }), 'Αθήνα');
    await user.type(screen.getByRole('textbox', { name: /Email/ }), 'ui.test@example.org');
  }

  it('checks the ΑΦΜ with the mod-11 rule and shows an error summary instead of calling the API', async () => {
    const api = mockApi([]);
    const { user, container } = renderScreen(<PartyCreatePage />, { path: '/parties/new', url: '/parties/new' });
    await fillValid(user, '094014202');
    await user.click(screen.getByRole('button', { name: 'Δημιουργία πελάτη' }));
    const summary = await screen.findByRole('region', { name: /σφάλμα|σφάλματα/ });
    expect(within(summary).getByText(/ΑΦΜ δεν είναι έγκυρος/)).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
    await expectNoA11yViolations(container);
  });

  it('requires the mandatory fields and at least one contact', async () => {
    mockApi([]);
    const { user } = renderScreen(<PartyCreatePage />, { path: '/parties/new', url: '/parties/new' });
    await user.click(screen.getByRole('button', { name: 'Δημιουργία πελάτη' }));
    const summary = await screen.findByRole('region', { name: /σφάλμα|σφάλματα/ });
    expect(within(summary).getAllByRole('link').length).toBeGreaterThanOrEqual(5);
    expect(within(summary).getByText('Συμπληρώστε τουλάχιστον ένα στοιχείο επικοινωνίας.')).toBeInTheDocument();
  });

  it('posts the person with an Idempotency-Key and reuses it when the same form is retried after an error', async () => {
    let attempt = 0;
    const api = mockApi([
      {
        method: 'POST',
        path: '/api/pty/v1/parties',
        respond: () =>
          ++attempt === 1
            ? problem(503, 'PTY-ERR-NOT-AVAILABLE', 'Η υπηρεσία δεν είναι διαθέσιμη')
            : { status: 201, body: { party: fx.party(), duplicateSuggestions: [], missingData: [] } },
      },
    ]);
    const { user } = renderScreen(<PartyCreatePage />, { path: '/parties/new', url: '/parties/new' });
    await fillValid(user);
    await user.click(screen.getByRole('button', { name: 'Δημιουργία πελάτη' }));
    expect(await screen.findByText('Η δημιουργία του πελάτη απέτυχε')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Δημιουργία πελάτη' }));
    await waitFor(() => {
      expect(screen.getByTestId('location')).toHaveTextContent(`/parties/${fx.partyId}`);
    });
    const [first, second] = api.callsTo('POST', '/api/pty/v1/parties');
    expect(first?.headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(second?.headers.get('Idempotency-Key')).toBe(first?.headers.get('Idempotency-Key'));
    expect(first?.body).toMatchObject({
      partyType: 'PERSON',
      person: { givenNames: 'Δοκιμή', familyName: 'Διεπαφής', birthDate: '1985-03-12' },
      identifiers: [{ scheme: 'AFM', value: '094014201' }],
      reason: 'NEW_CUSTOMER',
    });
  });

  it('offers the possible duplicates after creating', async () => {
    mockApi([
      {
        method: 'POST',
        path: '/api/pty/v1/parties',
        respond: () => ({ status: 201, body: { party: fx.party(), duplicateSuggestions: [fx.searchItem], missingData: [] } }),
      },
    ]);
    const { user } = renderScreen(<PartyCreatePage />, { path: '/parties/new', url: '/parties/new' });
    await fillValid(user);
    await user.click(screen.getByRole('button', { name: 'Δημιουργία πελάτη' }));
    expect(await screen.findByText('Βρέθηκε 1 πιθανό διπλότυπο')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /P000000021 · Δοκιμή Διεπαφής/ })).toBeInTheDocument();
  });
});

describe('PartyViewPage', () => {
  const view = (reveal: (purpose: string | null) => { status?: number; body?: unknown }) => ({
    method: 'GET',
    path: `/api/pty/v1/parties/${fx.partyId}`,
    respond: (r: { url: URL }) => reveal(r.url.searchParams.get('revealPurpose')),
  });

  it('masks personal data and reveals it only after a purpose is chosen', async () => {
    const api = mockApi([
      view((purpose) => ({ body: { party: fx.party(purpose !== null) } })),
    ]);
    const { user, container } = renderScreen(<PartyViewPage />, { path: '/parties/:partyId', url: `/parties/${fx.partyId}` });
    expect(await screen.findByRole('heading', { level: 1, name: 'Διεπαφής Δοκιμή' })).toBeInTheDocument();
    expect(screen.getByText('******201')).toBeInTheDocument();
    expect(screen.queryByText('094 014 201')).not.toBeInTheDocument();
    expect(screen.queryByText('12/03/1985')).not.toBeInTheDocument();
    await expectNoA11yViolations(container);

    await user.click(screen.getByRole('button', { name: 'Εμφάνιση προσωπικών δεδομένων' }));
    const dialog = await screen.findByRole('dialog', { name: 'Εμφάνιση προσωπικών δεδομένων' });
    const confirm = within(dialog).getByRole('button', { name: 'Εμφάνιση' });
    expect(confirm).toHaveAttribute('aria-disabled', 'true');
    await user.click(within(dialog).getByRole('button', { name: /Σκοπός εμφάνισης/ }));
    await user.click(await screen.findByRole('option', { name: 'Εξυπηρέτηση πελάτη' }));
    await user.click(within(dialog).getByRole('button', { name: 'Εμφάνιση' }));

    expect(await screen.findByText('094 014 201')).toBeInTheDocument();
    expect(screen.getByText('12/03/1985')).toBeInTheDocument();
    expect(screen.getAllByText('Εμφανίζονται προσωπικά δεδομένα').length).toBeGreaterThan(0);
    const reveals = api.calls.filter((c) => c.url.searchParams.has('revealPurpose'));
    expect(reveals).toHaveLength(1);
    expect(reveals[0]?.url.searchParams.get('revealPurpose')).toBe('CUSTOMER_SERVICE');

    await user.click(screen.getByRole('button', { name: 'Απόκρυψη προσωπικών δεδομένων' }));
    expect(screen.getByText('******201')).toBeInTheDocument();
  });

  it('explains a refused reveal (403) inside the dialog', async () => {
    mockApi([
      view((purpose) =>
        purpose ? problem(403, 'PTY-ERR-AUTHORITY-DENIED', 'Δεν έχετε την απαιτούμενη εξουσιοδότηση') : { body: { party: fx.party() } },
      ),
    ]);
    const { user } = renderScreen(<PartyViewPage />, { path: '/parties/:partyId', url: `/parties/${fx.partyId}` });
    await user.click(await screen.findByRole('button', { name: 'Εμφάνιση προσωπικών δεδομένων' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: /Σκοπός εμφάνισης/ }));
    await user.click(await screen.findByRole('option', { name: 'Έλεγχος συμμόρφωσης' }));
    await user.click(within(dialog).getByRole('button', { name: 'Εμφάνιση' }));
    expect(await within(dialog).findByText('Δεν έχετε δικαίωμα εμφάνισης προσωπικών δεδομένων.')).toBeInTheDocument();
    expect(screen.queryByText('094 014 201')).not.toBeInTheDocument();
  });

  it('shows not-found and error states', async () => {
    mockApi([view(() => problem(404, 'PTY-ERR-NOT-FOUND', 'Δεν βρέθηκε'))]);
    renderScreen(<PartyViewPage />, { path: '/parties/:partyId', url: `/parties/${fx.partyId}` });
    expect(await screen.findByText('Ο πελάτης δεν βρέθηκε')).toBeInTheDocument();
  });

  it('retries after a server error', async () => {
    let calls = 0;
    mockApi([view(() => (++calls === 1 ? problem(500, 'PLT-ERR-INTERNAL', 'Σφάλμα διακομιστή') : { body: { party: fx.party() } }))]);
    const { user } = renderScreen(<PartyViewPage />, { path: '/parties/:partyId', url: `/parties/${fx.partyId}` });
    await user.click(await screen.findByRole('button', { name: /Επανάληψη/ }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Διεπαφής Δοκιμή' })).toBeInTheDocument();
  });
});
