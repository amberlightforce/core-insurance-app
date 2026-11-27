import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { ProductCataloguePage } from './ProductCataloguePage';
import { ProductDetailPage } from './ProductDetailPage';

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

const loc = (en: string) => ({ el: en, en });
const describeBody = (illustrative: boolean) => ({
  product: 'MOTOR-GR',
  name: loc('Motor'),
  productType: 'PERSONAL',
  customerType: 'RETAIL',
  version: '1.2',
  artefactHash: hash,
  isAbstract: false,
  channels: ['DIRECT'],
  languages: ['el', 'en'],
  contractCurrency: 'EUR',
  terms: { allowed: ['P1Y'], default: 'P1Y' },
  windows: {
    newBusiness: { from: '2026-01-01', to: null },
    renewal: { from: '2026-01-01', to: null },
  },
  illustrative,
  orderedSteps: [{ code: 'RISK', name: loc('Risk'), kind: 'ELEMENTS' }],
  elements: [{ code: 'VEHICLE', name: loc('Vehicle'), minimum: 1, maximum: 3, fields: [] }],
  coverages: [
    {
      code: 'MTPL',
      name: loc('Motor third party'),
      existence: 'MANDATORY',
      removal: 'NONE',
      coveredElement: 'VEHICLE',
      terms: [
        {
          code: 'DEDUCTIBLE',
          name: loc('Deductible'),
          kind: 'DEDUCTIBLE',
          valueType: 'MONEY',
          modelType: 'OPTION',
          required: true,
          options: [{ code: 'D100', value: '100', name: loc('EUR 100'), illustrative: true }],
        },
      ],
    },
  ],
  questionSets: [
    {
      code: 'UW1',
      type: 'UNDERWRITING',
      name: loc('Underwriting'),
      questions: [
        {
          code: 'Q_CLAIMS',
          text: loc('Any claims in 5 years?'),
          answerType: 'CHOICE',
          required: true,
          customerVisible: true,
        },
      ],
    },
  ],
  paymentPlans: ['ANNUAL'],
});

const detailRoutes = (illustrative: boolean) => [
  {
    method: 'GET' as const,
    path: '/api/pfc/v1/products',
    respond: () => ({ body: { items: [product], nextCursor: null } }),
  },
  {
    method: 'POST' as const,
    path: '/api/pfc/v1/products/describe',
    respond: () => ({ body: describeBody(illustrative) }),
  },
];

describe('product detail', () => {
  it('shows the overview with the illustrative pill and the artefact hash, passes axe', async () => {
    mockApi(detailRoutes(true));
    const { container } = renderScreen(<ProductDetailPage />, {
      path: '/products/:code',
      url: '/products/MOTOR-GR',
    });
    expect(await screen.findByText('Terms and windows')).toBeInTheDocument();
    expect(screen.getAllByText('Illustrative data').length).toBeGreaterThan(0);
    expect(screen.getByText(hash)).toBeInTheDocument();
    expect(screen.getByText('Journey steps')).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });

  it('shows no illustrative pill for settled data and lists coverage terms', async () => {
    mockApi(detailRoutes(false));
    const { user } = renderScreen(<ProductDetailPage />, {
      path: '/products/:code',
      url: '/products/MOTOR-GR?tab=coverages',
    });
    expect(await screen.findByText('Coverage terms and options')).toBeInTheDocument();
    expect(screen.getByText('Motor third party')).toBeInTheDocument();
    expect(screen.getByText(/EUR 100/)).toBeInTheDocument();
    expect(screen.queryByText('Illustrative data', { selector: 'span' })).toBeNull();
    await user.click(screen.getByRole('tab', { name: /Versions/ }));
    expect(await screen.findByText('Version timeline')).toBeInTheDocument();
    expect(screen.getAllByText('Fall-back copy of 1.0').length).toBeGreaterThan(1);
  });

  it('shows the questions tab and a describe failure', async () => {
    mockApi(detailRoutes(false));
    renderScreen(<ProductDetailPage />, {
      path: '/products/:code',
      url: '/products/MOTOR-GR?tab=questions',
    });
    expect(await screen.findByText('Any claims in 5 years?')).toBeInTheDocument();
  });

  it('shows the describe failure as a problem banner', async () => {
    mockApi([
      ...detailRoutes(false).slice(0, 1),
      {
        method: 'POST',
        path: '/api/pfc/v1/products/describe',
        respond: () => problem(500, 'PFC-ERR-UNAVAILABLE', 'Synthetic describe unavailable'),
      },
    ]);
    renderScreen(<ProductDetailPage />, {
      path: '/products/:code',
      url: '/products/MOTOR-GR',
    });
    expect(await screen.findByText('Synthetic describe unavailable')).toBeInTheDocument();
  });
});
