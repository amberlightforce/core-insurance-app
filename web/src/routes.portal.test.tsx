import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { hasStaffAccess, isPortalCustomerOnly, landingPathFor } from './auth/roles';
import { routes } from './routes';
import { expectNoA11yViolations } from './test/axe';
import { mockApi } from './test/mockApi';
import { renderWithDs } from './test/render';

vi.setConfig({ testTimeout: 60_000 });

const customer = {
  id: 'customer',
  name: 'Dev Customer (synthetic)',
  roles: ['Customer.Portal'],
};
const underwriter = {
  id: 'underwriter',
  name: 'Dev Underwriter (synthetic)',
  roles: ['Staff.Underwriter'],
};

function signInAs(user: { id: string; name: string; roles: string[] } | null) {
  sessionStorage.clear();
  if (user) {
    sessionStorage.setItem(
      'coreins.devSession',
      JSON.stringify({ accessToken: 't', expiresAt: '2999-01-01T00:00:00Z', user }),
    );
  }
}

/** The REAL route tree (root routes.tsx), so mounting, redirects and guards are tested as shipped. */
function renderApp(url: string) {
  const router = createMemoryRouter(routes, { initialEntries: [url] });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  const view = renderWithDs(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
  return { ...view, router };
}

beforeEach(() => {
  // The staff shell and the portal bell talk to the API on mount; nothing here needs a real answer.
  mockApi([
    { method: 'GET', path: /.*/, respond: () => ({ status: 404 }) },
    { method: 'POST', path: /.*/, respond: () => ({ status: 404 }) },
  ]);
});

afterEach(() => {
  vi.unstubAllGlobals();
  sessionStorage.clear();
});

describe('role helpers', () => {
  it('sends only customers to the portal', () => {
    expect(isPortalCustomerOnly(['Customer.Portal'])).toBe(true);
    expect(isPortalCustomerOnly(['Customer.Portal', 'Staff.Billing'])).toBe(false);
    expect(hasStaffAccess(['Staff.Billing'])).toBe(true);
    expect(landingPathFor(['Customer.Portal'])).toBe('/portal');
    expect(landingPathFor(['Staff.Underwriter'])).toBe('/');
    expect(landingPathFor([])).toBe('/');
  });
});

describe('PortalGuard', () => {
  it('lets a customer into the portal and shows the portal shell (no staff rail)', async () => {
    signInAs(customer);
    const { container, router } = renderApp('/portal');
    expect(
      await screen.findByRole('heading', { level: 1, name: 'Η ασφάλισή σας, σε ένα σημείο' }),
    ).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/portal');
    expect(screen.getByRole('navigation', { name: 'Πύλη' })).toBeInTheDocument();
    expect(screen.queryByRole('navigation', { name: /Κύρια|Main/ })).toBeNull();
    await expectNoA11yViolations(container);
  });

  it('refuses a staff user: redirected to the staff home', async () => {
    signInAs(underwriter);
    const { router } = renderApp('/portal');
    await vi.waitFor(() => {
      expect(router.state.location.pathname).toBe('/');
    });
  });

  it('sends a signed-out visitor to sign-in', async () => {
    signInAs(null);
    const { router } = renderApp('/portal/policies');
    await vi.waitFor(() => {
      expect(router.state.location.pathname).toBe('/dev/sign-in');
    });
  });

  it('refuses an account with neither role (forbidden state)', async () => {
    signInAs({ id: 'nobody', name: 'Nobody', roles: [] });
    renderApp('/portal');
    expect(
      await screen.findByRole('heading', { level: 2, name: 'Η περιοχή αυτή είναι για πελάτες' }),
    ).toBeInTheDocument();
  });

  it('shows the coming-soon stub for areas BO-CHN-13 has not built and a not-found page', async () => {
    signInAs(customer);
    const first = renderApp('/portal/policies');
    expect(
      await screen.findByRole('heading', { name: 'Η ενότητα «Ασφαλιστήρια» έρχεται σύντομα' }),
    ).toBeInTheDocument();
    first.unmount();
    renderApp('/portal/nope');
    expect(
      await screen.findByRole('heading', { name: 'Η σελίδα δεν υπάρχει' }),
    ).toBeInTheDocument();
  });
});

describe('staff gate', () => {
  it('redirects a customer who opens a staff route to the portal', async () => {
    signInAs(customer);
    const { router } = renderApp('/policies');
    await vi.waitFor(() => {
      expect(router.state.location.pathname).toBe('/portal');
    });
  });
});

describe('public withdrawal declaration', () => {
  it('is reachable without sign-in, with its own shell and axe-clean', async () => {
    signInAs(null);
    const { container, router } = renderApp('/portal/withdrawal');
    expect(
      await screen.findByRole('heading', {
        level: 1,
        name: 'Υπαναχώρηση από σύμβαση εξ αποστάσεως',
      }),
    ).toBeInTheDocument();
    expect(router.state.location.pathname).toBe('/portal/withdrawal');
    expect(screen.getByRole('button', { name: 'Σύνδεση' })).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('redirects the old staff-app address /channels/withdrawal', async () => {
    signInAs(null);
    const { router } = renderApp('/channels/withdrawal');
    await vi.waitFor(() => {
      expect(router.state.location.pathname).toBe('/portal/withdrawal');
    });
  });

  it('has a footer link to it on every portal page', async () => {
    signInAs(customer);
    renderApp('/portal');
    const link = await screen.findByRole('link', { name: 'Δικαίωμα υπαναχώρησης' });
    expect(link).toHaveAttribute('href', '/portal/withdrawal');
  });
});
