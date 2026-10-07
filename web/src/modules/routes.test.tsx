import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { routes } from '../routes';
import { expectNoA11yViolations } from '../test/axe';
import { mockApi } from '../test/mockApi';
import { renderWithDs } from '../test/render';

vi.setConfig({ testTimeout: 30_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderAt(path: string) {
  mockApi([]);
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return renderWithDs(
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  );
}

describe('staff screens in the app shell', () => {
  it.each([
    ['/parties', 'Αναζήτηση πελατών', 'Πελάτες'],
    ['/parties/new', 'Νέο φυσικό πρόσωπο', 'Πελάτες'],
    ['/policies', 'Ασφαλιστήρια', 'Ασφαλιστήρια'],
    ['/billing', 'Χρεώσεις και εισπράξεις', 'Χρεώσεις'],
    ['/finance', 'Λογιστικές εγγραφές', 'Λογιστική'],
  ])('%s opens in the shell with its rail entry current', async (path, heading, railLabel) => {
    renderAt(path);
    expect(
      await screen.findByRole('heading', { level: 1, name: heading }, { timeout: 15_000 }),
    ).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' });
    expect(within(nav).getByRole('link', { name: railLabel })).toHaveAttribute(
      'aria-current',
      'page',
    );
  });

  it('opens the quote wizard under the policies rail entry', async () => {
    renderAt('/policies/quotes/new');
    expect(
      await screen.findByRole(
        'heading',
        { level: 2, name: 'Λήπτης και προϊόν' },
        { timeout: 15_000 },
      ),
    ).toBeInTheDocument();
    const nav = screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' });
    expect(within(nav).getByRole('link', { name: 'Ασφαλιστήρια' })).toHaveAttribute(
      'aria-current',
      'page',
    );
  });

  it('has no axe violations on the party search inside the shell', async () => {
    const { container } = renderAt('/parties');
    await screen.findByRole(
      'heading',
      { level: 1, name: 'Αναζήτηση πελατών' },
      { timeout: 15_000 },
    );
    await expectNoA11yViolations(container);
  });
});
