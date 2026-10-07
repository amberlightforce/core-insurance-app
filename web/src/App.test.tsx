import { screen, within } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { describe, expect, it } from 'vitest';

import { routes } from './routes';
import { expectNoA11yViolations } from './test/axe';
import { renderWithDs } from './test/render';

function renderAt(path: string) {
  const router = createMemoryRouter(routes, { initialEntries: [path] });
  return renderWithDs(<RouterProvider router={router} />);
}

describe('App', () => {
  it('renders the shell with the home placeholder in Greek', () => {
    renderAt('/');
    expect(screen.getByRole('main')).toHaveTextContent(
      'Η ενότητα «Αρχική» δεν είναι ακόμη διαθέσιμη',
    );
    expect(document.title).toBe('Αρχική · Core Insurance');
  });

  it('routes a module and marks it current', () => {
    renderAt('/claims');
    const nav = screen.getByRole('navigation', { name: 'Κύρια πλοήγηση' });
    expect(within(nav).getByRole('link', { name: 'Ζημίες' })).toHaveAttribute(
      'aria-current',
      'page',
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Ζημίες');
  });

  it('opens the command palette from the top bar and navigates', async () => {
    const { user } = renderAt('/');
    await user.click(screen.getByRole('button', { name: /Αναζήτηση ή εντολή/ }));
    const dialog = await screen.findByRole('dialog');
    await user.keyboard('ζημ');
    await user.click(within(dialog).getByRole('menuitem', { name: /Ζημίες/ }));
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Ζημίες');
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
