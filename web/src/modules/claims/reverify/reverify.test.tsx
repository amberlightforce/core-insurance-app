import { screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ClaimView } from '../../../api/types';
import { expectNoA11yViolations } from '../../../test/axe';
import { mockApi, problem, renderScreen, type MockRoute } from '../../../test/mockApi';
import type { SnapshotResponse } from './api';
import { ReverifyBanner } from './ReverifyBanner';

vi.setConfig({ testTimeout: 60_000 });

afterEach(() => {
  vi.unstubAllGlobals();
});

const claimId = 'c1a10000-1111-4222-8333-000000000001';
const oldRef = 'snap-old-1';
const newRef = 'snap-new-2';

function claim(
  status: 'PENDING' | 'VERIFIED' | 'REVERIFICATION_REQUIRED',
  withPending = true,
): ClaimView {
  return {
    summary: { claimId, snapshotStatus: status, recordVersion: 3 },
    ...(status === 'REVERIFICATION_REQUIRED' && withPending
      ? {
          pendingReverification: {
            oldSnapshotRef: oldRef,
            newSnapshotRef: newRef,
            causeEventId: 'e0000000-1111-4222-8333-000000000099',
            raisedAt: '2026-10-07T08:00:00Z',
          },
        }
      : {}),
  } as unknown as ClaimView;
}

function snapshot(
  ref: string,
  make: string,
  coverages: string[],
  value = '12000.00',
): SnapshotResponse {
  return {
    snapshotRef: ref,
    knownAt: '2026-10-05T09:00:00Z',
    inForce: true,
    content: {
      productVersion: 3,
      vehicles: [
        {
          make,
          model: 'Golf',
          firstRegistrationYear: 2019,
          value: { amount: value, currency: 'EUR' },
        },
      ],
      coverages: coverages.map((coverageCode) => ({ coverageCode, selected: true })),
    },
  } as unknown as SnapshotResponse;
}

const snapshotRoute = (reply?: () => { status?: number; body?: unknown }): MockRoute => ({
  method: 'GET',
  path: '/api/pol/v1/snapshots/get',
  respond: (request) =>
    reply
      ? reply()
      : {
          body:
            request.url.searchParams.get('snapshotRef') === oldRef
              ? snapshot(oldRef, 'VW', ['OWN-DAMAGE', 'WINDSCREEN'])
              : snapshot(newRef, 'VW', ['OWN-DAMAGE'], '9500.00'),
        },
});

const show = (view: ClaimView) =>
  renderScreen(<ReverifyBanner claim={view} />, {
    path: '/claims/:claimId',
    url: `/claims/${claimId}`,
  });

describe('ReverifyBanner', () => {
  it('renders nothing for a verified claim', () => {
    mockApi([]);
    show(claim('VERIFIED'));
    expect(screen.queryByRole('button', { name: 'Σύγκριση και απόφαση' })).not.toBeInTheDocument();
    expect(screen.queryByText('Η κάλυψη δεν έχει επαληθευτεί πλήρως')).not.toBeInTheDocument();
  });

  it('keeps the plain notice while verification is pending', () => {
    mockApi([]);
    show(claim('PENDING'));
    expect(screen.getByText('Η κάλυψη δεν έχει επαληθευτεί πλήρως')).toBeInTheDocument();
  });

  it('warns that the policy changed and says the claim keeps its basis until decided', async () => {
    mockApi([]);
    const { container } = show(claim('REVERIFICATION_REQUIRED'));
    expect(
      screen.getByText('Το ασφαλιστήριο άλλαξε μετά την επαλήθευση της ζημίας'),
    ).toBeInTheDocument();
    expect(screen.getByText(/διατηρεί την τρέχουσα βάση κάλυψης/)).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('compares old and new snapshots side by side and marks what changed', async () => {
    mockApi([snapshotRoute()]);
    const { user } = show(claim('REVERIFICATION_REQUIRED'));
    await user.click(screen.getByRole('button', { name: 'Σύγκριση και απόφαση' }));
    const current = await screen.findByRole('region', { name: 'Τρέχουσα βάση (στη ζημία)' });
    const next = screen.getByRole('region', { name: 'Νέο στιγμιότυπο ασφαλιστηρίου' });
    expect(within(current).getByText('Θραύση κρυστάλλων')).toBeInTheDocument();
    expect(within(next).getByText(/Θραύση κρυστάλλων: Δεν περιλαμβάνεται/)).toBeInTheDocument();
    expect(within(next).getByText('Ίδιες ζημιές')).toBeInTheDocument();
    expect(within(next).getByText('9.500,00 €')).toBeInTheDocument();
    expect(within(next).getAllByText('(άλλαξε)').length).toBeGreaterThan(0);
    expect(within(current).queryByText('(άλλαξε)')).not.toBeInTheDocument();
  });

  it('offers only the reasons the backend accepts for each decision', async () => {
    mockApi([snapshotRoute()]);
    const { user } = show(claim('REVERIFICATION_REQUIRED'));
    await user.click(screen.getByRole('button', { name: 'Σύγκριση και απόφαση' }));
    await screen.findByRole('region', { name: 'Νέο στιγμιότυπο ασφαλιστηρίου' });
    await user.click(screen.getByRole('radio', { name: /Υιοθέτηση του νέου στιγμιοτύπου/ }));
    await user.click(screen.getByRole('button', { name: /Λόγος/ }));
    expect(await screen.findAllByRole('option')).toHaveLength(2);
    expect(screen.queryByRole('option', { name: 'Κρίση του χειριστή' })).not.toBeInTheDocument();
  });

  it('hides the decision form when the comparison cannot be read', async () => {
    mockApi([snapshotRoute(() => problem(403, 'POL-ERR-FORBIDDEN', 'Forbidden'))]);
    const { user } = show(claim('REVERIFICATION_REQUIRED'));
    await user.click(screen.getByRole('button', { name: 'Σύγκριση και απόφαση' }));
    expect(await screen.findByText('Δεν έχετε δικαίωμα')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Καταγραφή απόφασης' })).not.toBeInTheDocument();
  });

  it('explains a changed-again policy in plain words with the code under technical details', async () => {
    mockApi([
      snapshotRoute(),
      {
        method: 'POST',
        path: '/api/clm/v1/coverage/reverify',
        respond: () =>
          problem(409, 'CLM-ERR-SNAPSHOT-MISMATCH', 'Conflict', 'expectedNewSnapshotRef is stale'),
      },
    ]);
    const { user } = show(claim('REVERIFICATION_REQUIRED'));
    await user.click(screen.getByRole('button', { name: 'Σύγκριση και απόφαση' }));
    await screen.findByRole('region', { name: 'Νέο στιγμιότυπο ασφαλιστηρίου' });
    await user.click(screen.getByRole('radio', { name: /Διατήρηση της τρέχουσας βάσης/ }));
    await user.click(screen.getByRole('button', { name: /Λόγος/ }));
    await user.click(await screen.findByRole('option', { name: 'Κρίση του χειριστή' }));
    await user.click(screen.getByRole('button', { name: 'Καταγραφή απόφασης' }));
    expect(await screen.findByText(/άλλαξε ξανά ενώ αποφασίζατε/)).toBeInTheDocument();
    const details = screen.getByText('Τεχνικές λεπτομέρειες').closest('details');
    expect(details).not.toHaveAttribute('open');
    expect(
      within(details as HTMLElement).getByText('CLM-ERR-SNAPSHOT-MISMATCH'),
    ).toBeInTheDocument();
  });

  it('requires a decision and a reason before sending', async () => {
    const api = mockApi([snapshotRoute()]);
    const { user } = show(claim('REVERIFICATION_REQUIRED'));
    await user.click(screen.getByRole('button', { name: 'Σύγκριση και απόφαση' }));
    await screen.findByRole('region', { name: 'Νέο στιγμιότυπο ασφαλιστηρίου' });
    await user.click(screen.getByRole('button', { name: 'Καταγραφή απόφασης' }));
    expect(await screen.findByText('Επιλέξτε διατήρηση ή υιοθέτηση.')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/clm/v1/coverage/reverify')).toHaveLength(0);
  });

  it('adopts the new snapshot with the reason, the expected ref and an Idempotency-Key', async () => {
    const api = mockApi([
      snapshotRoute(),
      {
        method: 'POST',
        path: '/api/clm/v1/coverage/reverify',
        respond: () => ({
          body: {
            claimId,
            decisionRecordId: 'd0000000-1111-4222-8333-000000000001',
            decision: 'ADOPT',
            snapshotStatus: 'VERIFIED',
            snapshotRef: newRef,
            coverageInQuestion: true,
            decidedAt: '2026-10-08T08:00:00Z',
          },
        }),
      },
    ]);
    const { user } = show(claim('REVERIFICATION_REQUIRED'));
    await user.click(screen.getByRole('button', { name: 'Σύγκριση και απόφαση' }));
    await screen.findByRole('region', { name: 'Νέο στιγμιότυπο ασφαλιστηρίου' });
    await user.click(screen.getByRole('radio', { name: /Υιοθέτηση του νέου στιγμιοτύπου/ }));
    await user.click(screen.getByRole('button', { name: /Λόγος/ }));
    await user.click(
      await screen.findByRole('option', { name: 'Η αλλαγή ισχύει για αυτή τη ζημία' }),
    );
    await user.type(screen.getByLabelText('Σχόλιο (προαιρετικό)'), 'Επιβεβαιώθηκε');
    await user.click(screen.getByRole('button', { name: 'Καταγραφή απόφασης' }));
    const [post] = await vi.waitFor(() => {
      const calls = api.callsTo('POST', '/api/clm/v1/coverage/reverify');
      expect(calls).toHaveLength(1);
      return calls;
    });
    expect(post?.body).toEqual({
      claimId,
      decision: 'ADOPT',
      reasonCode: 'CHANGE_APPLIES',
      comment: 'Επιβεβαιώθηκε',
      expectedNewSnapshotRef: newRef,
    });
    expect(post?.headers.get('Idempotency-Key')).toBeTruthy();
  });

  it('shows an error with a retry when a snapshot cannot be read', async () => {
    mockApi([snapshotRoute(() => problem(500, 'POL-ERR-INTERNAL', 'Boom'))]);
    const { user } = show(claim('REVERIFICATION_REQUIRED'));
    await user.click(screen.getByRole('button', { name: 'Σύγκριση και απόφαση' }));
    expect(
      await screen.findByRole('button', { name: /Δοκιμή ξανά|Επανάληψη/ }),
    ).toBeInTheDocument();
  });
});
