import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { ProductCataloguePage } from './ProductCataloguePage';

const hash = 'a'.repeat(64);
const version = (v: string, status: string, extra: Record<string, unknown> = {}) => ({
  version: v,
  status,
  isAbstract: false,
  channels: ['DIRECT'],
  contractCurrency: 'EUR',
  newBusinessFrom: '2025-01-01',
  newBusinessTo: null,
  renewalFrom: '2025-01-01',
  renewalTo: null,
  artefactHash: hash,
  fallbackOfVersion: null,
  replacesVersion: null,
  ...extra,
});
const product = {
  code: 'MOTOR-GR',
  jurisdiction: 'GR',
  lineCode: 'MOTOR',
  lineFamily: 'MOTOR',
  name: { el: 'Αυτοκίνητο', en: 'Motor' },
  productType: 'PERSONAL',
  customerType: 'RETAIL',
  versions: [
    version('1.2', 'LOCKED', { fallbackOfVersion: '1.0', newBusinessFrom: '2026-01-01' }),
    version('1.1', 'LOCKED', { newBusinessTo: '2026-01-01', replacesVersion: null }),
  ],
};

vi.mock('../../dev-auth/devAuth', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../dev-auth/devAuth')>()),
  useDevSession: () => ({
    user: { id: 'a', actorKey: 'USER:dev:a', name: 'Admin', roles: ['Platform.Admin'] },
  }),
}));
vi.setConfig({ testTimeout: 60_000 });

beforeEach(async () => {
  await i18n.changeLanguage('en');
});

describe('product catalogue', () => {
  it('lists products with versions, fall-back marker and pack links', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/pfc/v1/products',
        respond: () => ({ body: { items: [product], nextCursor: null } }),
      },
    ]);
    renderScreen(<ProductCataloguePage />, { path: '/products', url: '/products' });
    expect(await screen.findByText('Motor')).toBeInTheDocument();
    expect(screen.getByText('Fall-back copy of 1.0')).toBeInTheDocument();
    expect(screen.getByText('In force: 1.2')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Market packs' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Pack rollback exceptions' })).toBeInTheDocument();
  });

  it('shows an empty state', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/pfc/v1/products',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    renderScreen(<ProductCataloguePage />, { path: '/products', url: '/products' });
    expect(await screen.findByText('No products yet')).toBeInTheDocument();
  });

  it('shows a request failure', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/pfc/v1/products',
        respond: () => problem(500, 'PFC-ERR-UNAVAILABLE', 'Synthetic catalogue unavailable'),
      },
    ]);
    renderScreen(<ProductCataloguePage />, { path: '/products', url: '/products' });
    expect(await screen.findByText('Synthetic catalogue unavailable')).toBeInTheDocument();
  });
});
