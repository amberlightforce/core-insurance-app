import { screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { DocumentDetailPage } from './DocumentDetailPage';
import { DocumentsHomePage } from './DocumentsHomePage';
import { SubjectDocumentsPage } from './SubjectDocumentsPage';
import type { DocumentView } from './api';

const session = vi.hoisted(() => ({ roles: ['Platform.Admin'] as string[] }));
vi.mock('../../dev-auth/devAuth', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../dev-auth/devAuth')>()),
  useDevSession: () => ({
    user: { id: 'u1', actorKey: 'USER:dev:u1', name: 'Synthetic user', roles: session.roles },
  }),
}));

vi.setConfig({ testTimeout: 30_000 });

beforeEach(async () => {
  session.roles = ['Platform.Admin'];
  await i18n.changeLanguage('en');
});
afterEach(() => {
  vi.unstubAllGlobals();
});

const id = '3f6c8a52-1111-4222-8333-444455556666';
const doc: DocumentView = {
  documentId: id,
  documentNumber: 'DOC-000001',
  documentType: 'POLICY_SCHEDULE',
  requestNo: 'REQ-1',
  subject: 'POL/Policy/POL000000007',
  recipientPartyId: '9f6c8a52-1111-4222-8333-444455556666',
  language: 'el',
  status: 'Rendered',
  medium: 'REGISTERED_POST',
  archiveId: '7f6c8a52-1111-4222-8333-444455556666',
  contentHash: 'sha256:abc',
  createdAt: '2026-10-07T09:00:00Z',
  deliveries: [
    {
      deliveryId: 'd1',
      channel: 'POST',
      medium: 'PRINT',
      status: 'Delivered',
      proofLevel: 'REGISTERED',
      notificationDate: '2026-10-08',
      failureReason: null,
      isReprint: false,
    },
  ],
};
const hiddenDoc: DocumentView = {
  ...doc,
  documentId: '5f6c8a52-1111-4222-8333-444455556667',
  documentNumber: 'DOC-000002',
  hidden: true,
  withdrawnReason: 'Sent in error',
  customerVisible: false,
};

const typeRoute = {
  method: 'GET',
  path: '/api/doc/v1/document-types',
  respond: () => ({
    body: {
      items: [
        {
          documentType: 'POLICY_SCHEDULE',
          defaultMedium: 'EMAIL',
          titleEl: 'x',
          titleEn: 'x',
          requiredFields: [],
          customerVisible: true,
        },
      ],
      nextCursor: null,
    },
  }),
};
const searchRoutes = (onSearch?: (u: URL) => void) => [
  typeRoute,
  {
    method: 'GET',
    path: '/api/doc/v1/documents/search',
    respond: (r: { url: URL }) => {
      onSearch?.(r.url);
      if (r.url.searchParams.get('cursor') === 'c2')
        return { body: { items: [hiddenDoc], nextCursor: null } };
      return { body: { items: [doc], nextCursor: 'c2' } };
    },
  },
];

describe('DocumentsHomePage (library)', () => {
  const page = () => renderScreen(<DocumentsHomePage />, { path: '/documents', url: '/documents' });

  it('lists documents with a masked recipient and loads the next page by cursor', async () => {
    const api = mockApi(searchRoutes());
    const { user } = page();
    expect(await screen.findByText('DOC-000001')).toBeInTheDocument();
    expect(screen.getByText('9f6c8a52-****')).toBeInTheDocument();
    expect(screen.queryByText('9f6c8a52-1111-4222-8333-444455556666')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('DOC-000002')).toBeInTheDocument();
    expect(screen.getByText('DOC-000001')).toBeInTheDocument();
    expect(screen.getByText('Withdrawn')).toBeInTheDocument();
    expect(screen.getByText('Hidden')).toBeInTheDocument();
    const second = api.callsTo('GET', '/api/doc/v1/documents/search')[1];
    expect(second?.url.searchParams.get('cursor')).toBe('c2');
  });

  it('sends the filters and the show-hidden toggle to the search', async () => {
    const seen: URL[] = [];
    mockApi(
      searchRoutes((u) => {
        seen.push(u);
      }),
    );
    const { user } = page();
    await screen.findByText('DOC-000001');
    await user.type(screen.getByLabelText('Document number'), 'DOC-0');
    await user.click(screen.getByRole('button', { name: 'Apply filters' }));
    await waitFor(() => {
      expect(seen.some((u) => u.searchParams.get('q') === 'DOC-0')).toBe(true);
    });
    await user.click(screen.getByRole('switch', { name: 'Show hidden documents' }));
    await waitFor(() => {
      expect(seen.some((u) => u.searchParams.get('includeHidden') === 'true')).toBe(true);
    });
  });

  it('shows the empty state', async () => {
    mockApi([
      typeRoute,
      {
        method: 'GET',
        path: '/api/doc/v1/documents/search',
        respond: () => ({ body: { items: [], nextCursor: null } }),
      },
    ]);
    page();
    expect(await screen.findByText('No documents match')).toBeInTheDocument();
  });

  it('shows the problem with a retry when the search fails', async () => {
    mockApi([
      typeRoute,
      {
        method: 'GET',
        path: '/api/doc/v1/documents/search',
        respond: () => problem(500, 'DOC-ERR-INTERNAL', 'Server error'),
      },
    ]);
    page();
    expect(await screen.findByRole('button', { name: /retry|try again/i })).toBeInTheDocument();
  });

  it('explains a 403 as missing permission', async () => {
    mockApi([
      typeRoute,
      {
        method: 'GET',
        path: '/api/doc/v1/documents/search',
        respond: () => problem(403, 'AUTH-FORBIDDEN', 'Forbidden'),
      },
    ]);
    page();
    expect((await screen.findAllByText(/permission/i)).length).toBeGreaterThan(0);
  });

  it('keeps the object-ref lookup and validates the document id', async () => {
    mockApi(searchRoutes());
    const { user } = page();
    await user.type(await screen.findByLabelText('Document id'), 'nope');
    await user.click(screen.getByRole('button', { name: 'Open document' }));
    expect(await screen.findByText('Enter a valid document id.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Show documents' })).toBeInTheDocument();
  });

  it('runs the completeness check for an administrator', async () => {
    const api = mockApi([
      ...searchRoutes(),
      {
        method: 'GET',
        path: '/api/doc/v1/archives/completeness',
        respond: () => ({
          body: {
            from: '2026-08-01',
            to: '2026-10-31',
            truncated: false,
            periods: [
              {
                period: '2026-10',
                issued: 4,
                ok: 3,
                orphanArchiveIds: [],
                gaps: [
                  {
                    documentId: id,
                    documentNumber: 'DOC-000001',
                    archiveId: '7f6c8a52-1111-4222-8333-444455556666',
                    kind: 'HASH_MISMATCH',
                  },
                ],
              },
            ],
          },
        }),
      },
    ]);
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: 'Run check' }));
    expect(await screen.findByText('2026-10')).toBeInTheDocument();
    expect(screen.getByText('1 gap')).toBeInTheDocument();
    expect(api.callsTo('GET', '/api/doc/v1/archives/completeness')).toHaveLength(1);
  });

  it('hides the completeness panel from non-administrators', async () => {
    session.roles = ['Staff.Billing'];
    mockApi(searchRoutes());
    page();
    await screen.findByText('DOC-000001');
    expect(screen.queryByRole('button', { name: 'Run check' })).not.toBeInTheDocument();
  });

  it('has no accessibility violations', async () => {
    mockApi(searchRoutes());
    const { container } = page();
    await screen.findByText('DOC-000001');
    await expectNoA11yViolations(container);
  });
});

describe('SubjectDocumentsPage', () => {
  const page = () =>
    renderScreen(<SubjectDocumentsPage />, {
      path: '/documents/subject/:module/:type/:id',
      url: '/documents/subject/POL/Policy/POL000000007',
    });

  it('lists the documents of the object by objectRef', async () => {
    const api = mockApi([
      {
        method: 'GET',
        path: '/api/doc/v1/documents/list-for-object',
        respond: () => ({ body: { items: [doc], nextCursor: null } }),
      },
    ]);
    page();
    expect(await screen.findByText('DOC-000001')).toBeInTheDocument();
    expect(
      api
        .callsTo('GET', '/api/doc/v1/documents/list-for-object')[0]
        ?.url.searchParams.get('objectRef'),
    ).toBe('POL/Policy/POL000000007');
  });

  it('shows the problem with a retry when the read fails', async () => {
    mockApi([
      {
        method: 'GET',
        path: '/api/doc/v1/documents/list-for-object',
        respond: () => problem(500, 'DOC-ERR-INTERNAL', 'Server error'),
      },
    ]);
    page();
    expect(await screen.findByRole('button', { name: /retry|try again/i })).toBeInTheDocument();
  });
});

describe('DocumentDetailPage', () => {
  const page = () =>
    renderScreen(<DocumentDetailPage />, {
      path: '/documents/:documentId',
      url: `/documents/${id}`,
    });
  const routes = () => [
    { method: 'GET', path: `/api/doc/v1/documents/${id}`, respond: () => ({ body: doc }) },
    {
      method: 'GET',
      path: `/api/doc/v1/evidence-packs/${id}`,
      respond: () => ({
        body: {
          sealedArchive: {
            document: doc,
            final: true,
            evidence: [
              {
                evidenceId: 'e1',
                deliveryId: 'd1',
                provider: 'stub-post',
                providerEventId: 'p1',
                eventType: 'DELIVERED',
                proofDate: '2026-10-08',
                proofReference: 'RR123',
                outcome: 'Delivered',
                receivedAt: '2026-10-08T10:00:00Z',
              },
            ],
          },
        },
      }),
    },
  ];

  it('shows the record, deliveries and provider evidence', async () => {
    mockApi(routes());
    page();
    expect(
      await screen.findByRole('heading', { level: 1, name: /POLICY_SCHEDULE/ }),
    ).toBeInTheDocument();
    expect(await screen.findByText('RR123')).toBeInTheDocument();
    expect(screen.getByText('sha256:abc')).toBeInTheDocument();
  });

  it('requires a reason and sends the reprint with an idempotency key', async () => {
    const api = mockApi([
      ...routes(),
      {
        method: 'POST',
        path: '/api/doc/v1/documents/reprint',
        respond: () => ({ body: { link: {} } }),
      },
    ]);
    const { user } = page();
    const submit = await screen.findByRole('button', { name: 'Request reprint' });
    await user.click(submit);
    expect(await screen.findByText('Give a reason (at least 3 characters).')).toBeInTheDocument();
    expect(api.callsTo('POST', '/api/doc/v1/documents/reprint')).toHaveLength(0);
    await user.type(screen.getByLabelText(/Reason/), 'Lost in post');
    await user.click(submit);
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/doc/v1/documents/reprint')).toHaveLength(1);
    });
    const call = api.callsTo('POST', '/api/doc/v1/documents/reprint')[0];
    expect(call?.body).toEqual({ documentId: id, reason: 'Lost in post' });
    expect(call?.headers.get('Idempotency-Key')).toBeTruthy();
    expect(await screen.findByRole('status', { name: 'Reprint requested' })).toBeInTheDocument();
  });

  it('resends with a reason and the chosen channel', async () => {
    const api = mockApi([
      ...routes(),
      {
        method: 'POST',
        path: '/api/doc/v1/documents/resend',
        respond: () => ({ body: { deliveryId: 'd2', medium: 'REGISTERED_POST' } }),
      },
    ]);
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: 'Resend' }));
    const dialog = await screen.findByRole('dialog');
    await user.click(within(dialog).getByRole('button', { name: 'Resend' }));
    expect(
      await within(dialog).findByText('Give a reason (at least 3 characters).'),
    ).toBeInTheDocument();
    await user.type(within(dialog).getByLabelText(/Reason/), 'Customer lost the mail');
    await user.click(within(dialog).getByRole('button', { name: 'Resend' }));
    await waitFor(() => {
      expect(api.callsTo('POST', '/api/doc/v1/documents/resend')).toHaveLength(1);
    });
    const call = api.callsTo('POST', '/api/doc/v1/documents/resend')[0];
    expect(call?.body).toMatchObject({
      documentId: id,
      reason: 'Customer lost the mail',
      channel: 'REGISTERED_POST',
    });
    expect(call?.headers.get('Idempotency-Key')).toBeTruthy();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('keeps the dialog open and explains a refused withdraw', async () => {
    mockApi([
      ...routes(),
      {
        method: 'POST',
        path: '/api/doc/v1/documents/withdraw',
        respond: () => problem(403, 'DOC-ROLE-REQUIRED', 'Administrator role required'),
      },
    ]);
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: 'Withdraw' }));
    const dialog = await screen.findByRole('alertdialog');
    await user.type(within(dialog).getByLabelText(/Reason/), 'Wrong customer');
    await user.click(within(dialog).getByRole('button', { name: 'Withdraw document' }));
    expect(await within(dialog).findByText(/Withdrawing failed/)).toBeInTheDocument();
    expect(screen.getByRole('alertdialog')).toBeInTheDocument();
  });

  it('verifies the archive integrity', async () => {
    const api = mockApi([
      ...routes(),
      {
        method: 'POST',
        path: '/api/doc/v1/archives/verify',
        respond: () => ({
          body: { archiveId: doc.archiveId, hash: 'a', computedHash: 'a', result: 'OK' },
        }),
      },
    ]);
    const { user } = page();
    await user.click(await screen.findByRole('button', { name: 'Verify integrity' }));
    expect((await screen.findAllByText('Integrity verified')).length).toBeGreaterThan(0);
    expect(api.callsTo('POST', '/api/doc/v1/archives/verify')[0]?.body).toEqual({
      archiveId: doc.archiveId,
    });
  });

  it('marks a superseded document and links to its replacement', async () => {
    const replaced: DocumentView = { ...doc, supersededByDocumentId: hiddenDoc.documentId };
    mockApi([
      { method: 'GET', path: `/api/doc/v1/documents/${id}`, respond: () => ({ body: replaced }) },
      ...routes().slice(1),
    ]);
    page();
    expect(await screen.findByText('This document was replaced')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Withdraw' })).toBeDisabled();
  });
});
