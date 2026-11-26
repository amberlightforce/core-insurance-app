import { screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../test/axe';
import { renderWithDs } from '../test/render';
import { apiFetch, readSession } from './devAuth';
import { DevSignIn } from './DevSignIn';

const underwriter = {
  id: 'underwriter',
  name: 'Dev Underwriter (synthetic)',
  roles: ['Staff.Underwriter'],
};
const billing = { id: 'billing', name: 'Dev Billing Clerk (synthetic)', roles: ['Staff.Billing'] };
const users = { items: [underwriter, billing] };
function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

/** The sign-in page plus a home route, so a successful sign-in can be seen landing there. */
function renderPage(path = '/dev/sign-in') {
  return renderWithDs(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/dev/sign-in" element={<DevSignIn />} />
        <Route path="/" element={<p>home page</p>} />
      </Routes>
    </MemoryRouter>,
  );
}

function stubSignIn(user: typeof underwriter, token: string) {
  const fetchMock = vi.fn((url: string, init?: RequestInit) => {
    if (url.endsWith('/dev/users')) return Promise.resolve(json(users));
    if (url.endsWith('/dev/sign-in') && init?.method === 'POST') {
      return Promise.resolve(json({ accessToken: token, expiresAt: '2999-01-01T00:00:00Z', user }));
    }
    return Promise.resolve(json({}));
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

describe('DevSignIn', () => {
  beforeEach(() => {
    sessionStorage.clear();
    localStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('explains a stale token when the api sent the user here (401)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(json(users))),
    );
    window.history.pushState({}, '', '/dev/sign-in?expired=1');
    try {
      renderPage();
      expect(await screen.findByText('Η σύνδεση έληξε')).toBeInTheDocument();
    } finally {
      window.history.pushState({}, '', '/');
    }
  });

  it('signs in on a click on a persona and goes straight to home', async () => {
    const fetchMock = stubSignIn(underwriter, 'token-1');

    const { user, container } = renderPage();
    expect(await screen.findByRole('heading', { level: 1, name: 'Σύνδεση' })).toBeInTheDocument();
    await expectNoA11yViolations(container);

    await user.click(await screen.findByRole('option', { name: /^Underwriter/ }));

    expect(await screen.findByText('home page')).toBeInTheDocument();
    expect(readSession()?.accessToken).toBe('token-1');

    await apiFetch('/api/pty/v1/parties/search?name=x');
    const headers = new Headers(fetchMock.mock.calls.at(-1)?.[1]?.headers);
    expect(headers.get('Authorization')).toBe('Bearer token-1');
  });

  it('filters personas by role and signs in with Enter', async () => {
    stubSignIn(billing, 'token-2');

    const { user } = renderPage();
    const search = await screen.findByRole('searchbox');
    await user.type(search, 'billing');
    expect(screen.getAllByRole('option')).toHaveLength(1);
    await user.keyboard('{ArrowDown}{Enter}');

    expect(await screen.findByText('home page')).toBeInTheDocument();
    expect(readSession()?.accessToken).toBe('token-2');
    expect(localStorage.getItem('coreins.devLastUser')).toBe('billing');
  });

  it('keeps the user on the page with an error when sign-in fails', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((url: string) =>
        Promise.resolve(url.endsWith('/dev/users') ? json(users) : json({}, 500)),
      ),
    );
    const { user } = renderPage();
    await user.click(await screen.findByRole('option', { name: /^Underwriter/ }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Η σύνδεση απέτυχε');
    expect(readSession()).toBeNull();
  });

  it('shows an empty state when the API does not offer dev sign-in', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(json({}, 401))),
    );
    renderPage();
    expect(await screen.findByText('Η τοπική σύνδεση δεν είναι διαθέσιμη')).toBeInTheDocument();
  });

  it('shows an error state with retry when the users cannot be loaded', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(json({}, 500))),
    );
    renderPage();
    expect(await screen.findByText(/Οι χρήστες ανάπτυξης δεν φορτώθηκαν/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Δοκιμάστε ξανά|Επανάληψη/ })).toBeInTheDocument();
  });
});
