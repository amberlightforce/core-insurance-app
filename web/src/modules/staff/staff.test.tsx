import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { ApiError } from '../../api/client';
import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import { renderWithDs } from '../../test/render';
import { ProblemBanner } from './ProblemBanner';
import { readRecent, rememberRecent } from './recent';

describe('ProblemBanner', () => {
  const problem = new ApiError({
    status: 409,
    code: 'POL-ERR-QUOTE-STALE',
    title: 'Quote stale',
    detail: 'The quote validity has ended.',
    traceId: 'trace-1',
    errors: [{ field: 'riskTree.vehicles[0]', code: 'X', message: 'Vehicle incomplete' }],
  });

  it('renders RFC 9457 Problem Details readably: localised headline, detail, field errors, code and trace id', async () => {
    const onRetry = vi.fn();
    const { container, user } = renderWithDs(<ProblemBanner error={problem} onRetry={onRetry} />);
    expect(
      screen.getByText(
        'Η προσφορά δεν είναι πλέον ισχύουσα. Υπολογίστε νέα προσφορά πριν τη δέσμευση.',
      ),
    ).toBeInTheDocument();
    expect(screen.getByText('The quote validity has ended.')).toBeInTheDocument();
    expect(screen.getByText('riskTree.vehicles[0]')).toBeInTheDocument();
    expect(screen.getByText(/Vehicle incomplete/)).toBeInTheDocument();
    expect(screen.getByText('POL-ERR-QUOTE-STALE')).toBeInTheDocument();
    expect(screen.getByText(/Αναγνωριστικό παρακολούθησης: trace-1/)).toBeInTheDocument();
    await expectNoA11yViolations(container);
    await user.click(screen.getByRole('button', { name: 'Δοκιμή ξανά' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('uses the server title for unknown codes, and English when the UI language is English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <ProblemBanner
        error={new ApiError({ status: 422, code: 'XYZ-ERR-UNKNOWN', title: 'Server title' })}
      />,
    );
    expect(screen.getByText('Server title')).toBeInTheDocument();
    renderWithDs(<ProblemBanner error={problem} />);
    expect(screen.getByText(/Calculate a new quote before binding/)).toBeInTheDocument();
  });

  it('reports a network failure and unexpected errors', () => {
    renderWithDs(<ProblemBanner error={new ApiError({ status: 0, code: 'NETWORK' })} />);
    expect(screen.getByText('Δεν υπάρχει σύνδεση με τον διακομιστή')).toBeInTheDocument();
  });
});

describe('ProblemBanner 403', () => {
  it('explains a body-less 403 with the signed-in role instead of a bare failure', async () => {
    await i18n.changeLanguage('en');
    sessionStorage.setItem(
      'coreins.devSession',
      JSON.stringify({
        accessToken: 't',
        expiresAt: new Date(Date.now() + 60_000).toISOString(),
        user: { id: 'billing', name: 'Billing', roles: ['Staff.Billing'] },
      }),
    );
    renderWithDs(
      <ProblemBanner error={new ApiError({ status: 403 })} title="Customer creation failed" />,
    );
    expect(screen.getByText('Customer creation failed')).toBeInTheDocument();
    expect(screen.getByText(/Staff\.Billing.*does not allow this action/)).toBeInTheDocument();
    sessionStorage.clear();
  });
});

describe('recent records', () => {
  it('keeps the last records, newest first, without duplicates, and survives unavailable storage', () => {
    rememberRecent('policy', '1');
    rememberRecent('policy', '2');
    rememberRecent('policy', '1');
    expect(readRecent('policy')).toEqual(['1', '2']);
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked');
    });
    expect(readRecent('policy')).toEqual([]);
    vi.restoreAllMocks();
  });

  it('stores opaque ids only and drops entries left by the earlier id + number format', () => {
    localStorage.setItem(
      'coreins.recent.policy',
      JSON.stringify([{ id: '9', label: 'POL000000009' }]),
    );
    expect(readRecent('policy')).toEqual([]);
    rememberRecent('policy', 'abc');
    expect(localStorage.getItem('coreins.recent.policy')).toBeNull();
    expect(localStorage.getItem('coreins.recent.v2.policy')).toBe('["abc"]');
    localStorage.setItem('coreins.recent.v2.claim', '{"not":"a list"}');
    expect(readRecent('claim')).toEqual([]);
    localStorage.setItem('coreins.recent.v2.claim', '[1,"ok",null]');
    expect(readRecent('claim')).toEqual(['ok']);
    localStorage.clear();
  });
});
