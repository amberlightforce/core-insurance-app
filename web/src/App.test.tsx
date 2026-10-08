import { screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';

import { defaultNavItems, visibleNavItems } from './app-shell/navigation';
import { routes } from './routes';
import { expectNoA11yViolations } from './test/axe';
import { renderWithDs } from './test/render';

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  return renderWithDs(<RouterProvider router={router} />);
}

function signInAs(roles: string[]) {
  sessionStorage.setItem(
    'coreins.devSession',
    JSON.stringify({
      accessToken: 'test-token',
      expiresAt: new Date(Date.now() + 3_600_000).toISOString(),
      user: { id: 'dev', name: 'Dev', roles },
    }),
  );
}

afterEach(() => {
  sessionStorage.clear();
});

describe('App', () => {
  it('renders the shell with the home placeholder in Greek', () => {
    renderAt('/');
    expect(screen.getByRole('main')).toHaveTextContent(
      'Η ενότητα «Αρχική» δεν είναι ακόμη διαθέσιμη',
    );
    expect(document.title).toBe('Αρχική · Core Insurance');
  });

  it('routes a module and marks it current', () => {
    renderAt('/underwriting');
    const nav = screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' });
    expect(within(nav).getByRole('link', { name: 'Ανάληψη κινδύνου' })).toHaveAttribute(
      'aria-current',
      'page',
    );
  });

  it('shows the claims entry only to claims roles', () => {
    renderAt('/');
    expect(
      within(screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' })).queryByRole('link', {
        name: 'Ζημίες',
      }),
    ).not.toBeInTheDocument();
  });

  it.each(['Staff.ClaimsHandler', 'Staff.ClaimsManager'])(
    'shows the claims entry to %s',
    (role) => {
      signInAs([role]);
      renderAt('/');
      expect(
        within(screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' })).getByRole('link', {
          name: 'Ζημίες',
        }),
      ).toBeInTheDocument();
    },
  );

  it('shows every navigation entry to the all-roles dev super user', () => {
    const superRoles = [
      'Staff.Underwriter',
      'Staff.UnderwritingManager',
      'Staff.Billing',
      'Staff.Finance',
      'Staff.ClaimsHandler',
      'Staff.ClaimsManager',
      'Platform.Admin',
    ];
    expect(visibleNavItems(defaultNavItems, superRoles).map((item) => item.id)).toEqual(
      defaultNavItems.map((item) => item.id),
    );
    signInAs(superRoles);
    renderAt('/');
    expect(
      within(screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' })).getByRole('link', {
        name: 'Ζημίες',
      }),
    ).toBeInTheDocument();
  });

  it('opens the command palette from the top bar and navigates', async () => {
    const { user } = renderAt('/');
    await user.click(screen.getByRole('button', { name: /Αναζήτηση ή εντολή/ }));
    const dialog = await screen.findByRole('dialog');
    await user.keyboard('ανάλ');
    await user.click(within(dialog).getByRole('menuitem', { name: /Ανάληψη κινδύνου/ }));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Ανάληψη κινδύνου');
  });

  it('opens the shortcut help with the «?» button', async () => {
    const { user } = renderAt('/');
    await user.click(screen.getByRole('button', { name: 'Βοήθεια και συντομεύσεις' }));
    expect(await screen.findByRole('dialog')).toHaveTextContent('Αναζήτηση ή εντολή');
  });

  it('has no axe violations', async () => {
    const { container } = renderAt('/policies');
    await expectNoA11yViolations(container);
  });
});
