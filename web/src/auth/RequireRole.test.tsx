import { screen } from '@testing-library/react';
import { Route, Routes } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';

import type { DevSession } from '../dev-auth/devAuth';
import { renderScreen } from '../test/mockApi';
import { RequireRole } from './RequireRole';

function signIn(roles: string[]) {
  const session: DevSession = {
    accessToken: 'test',
    expiresAt: '2099-01-01T00:00:00Z',
    user: { id: 'u', name: 'u', roles, actorKey: 'USER:dev:u' },
  };
  sessionStorage.setItem('coreins.devSession', JSON.stringify(session));
}

function renderGuarded() {
  renderScreen(
    <Routes>
      <Route element={<RequireRole roles={['Staff.A', 'Staff.B']} />}>
        <Route index element={<p>secret screen</p>} />
      </Route>
    </Routes>,
    { path: '/x/*', url: '/x' },
  );
}

afterEach(() => {
  sessionStorage.clear();
});

describe('RequireRole', () => {
  it('renders the nested screen for a holder of any listed role', () => {
    signIn(['Staff.B']);
    renderGuarded();
    expect(screen.getByText('secret screen')).toBeInTheDocument();
  });

  it('shows the no-permission state to other roles', () => {
    signIn(['Staff.C']);
    renderGuarded();
    expect(screen.queryByText('secret screen')).not.toBeInTheDocument();
    expect(screen.getByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });

  it('fails closed when nobody is signed in', () => {
    renderGuarded();
    expect(screen.queryByText('secret screen')).not.toBeInTheDocument();
    expect(screen.getByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
  });
});
