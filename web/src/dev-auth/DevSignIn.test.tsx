import { screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../test/axe';
import { renderWithDs } from '../test/render';
import { apiFetch, readSession } from './devAuth';
import { DevSignIn } from './DevSignIn';

const users = {
  items: [
    { id: 'underwriter', name: 'Dev Underwriter (synthetic)', roles: ['Staff.Underwriter'] },
    { id: 'billing', name: 'Dev Billing Clerk (synthetic)', roles: ['Staff.Billing'] },
  ],
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

describe('DevSignIn', () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('signs in a configured user and keeps the token for API calls', async () => {
    const fetchMock = vi.fn((url: string, init?: RequestInit) => {
      if (url.endsWith('/dev/users')) return Promise.resolve(json(users));
      if (url.endsWith('/dev/sign-in') && init?.method === 'POST') {
        return Promise.resolve(
          json({ accessToken: 'token-1', expiresAt: '2999-01-01T00:00:00Z', user: users.items[0] }),
        );
      }
      return Promise.resolve(json({}));
    });
    vi.stubGlobal('fetch', fetchMock);

    const { user, container } = renderWithDs(<DevSignIn />);
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Τοπική σύνδεση ανάπτυξης' }),
    ).toBeInTheDocument();
    await expectNoA11yViolations(container);

    await user.click(screen.getByRole('button', { name: /Χρήστης/ }));
    await user.click(await screen.findByRole('option', { name: /Dev Underwriter/ }));
    await user.click(screen.getByRole('button', { name: 'Σύνδεση' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Dev Underwriter (synthetic)');
    expect(readSession()?.accessToken).toBe('token-1');

    await apiFetch('/api/pty/v1/parties/search?name=x');
    const headers = new Headers(fetchMock.mock.calls.at(-1)?.[1]?.headers);
    expect(headers.get('Authorization')).toBe('Bearer token-1');
  });

  it('shows an empty state when the API does not offer dev sign-in', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(json({}, 401))),
    );
    renderWithDs(<DevSignIn />);
    expect(await screen.findByText('Η τοπική σύνδεση δεν είναι διαθέσιμη')).toBeInTheDocument();
  });

  it('shows an error state with retry when the users cannot be loaded', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(json({}, 500))),
    );
    renderWithDs(<DevSignIn />);
    expect(await screen.findByText(/Οι χρήστες ανάπτυξης δεν φορτώθηκαν/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Δοκιμάστε ξανά|Επανάληψη/ })).toBeInTheDocument();
  });
});
