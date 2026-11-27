import { screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../i18n';
import { mockApi, problem, renderScreen } from '../../test/mockApi';
import { DocumentDetailPage } from './DocumentDetailPage';
import { DocumentsHomePage } from './DocumentsHomePage';
import { SubjectDocumentsPage } from './SubjectDocumentsPage';
import type { DocumentView } from './api';

vi.setConfig({ testTimeout: 30_000 });

beforeEach(async () => {
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
  status: 'Issued',
  medium: 'PRINT',
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

describe('DocumentsHomePage', () => {
  it('validates the document id before opening', async () => {
    const { user } = renderScreen(<DocumentsHomePage />, { path: '/documents', url: '/documents' });
    await user.type(await screen.findByLabelText('Document id'), 'nope');
    await user.click(screen.getByRole('button', { name: 'Open document' }));
    expect(await screen.findByText('Enter a valid document id.')).toBeInTheDocument();
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
    expect(screen.getByText('Issued')).toBeInTheDocument();
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
    expect(await screen.findByText('Reprint requested')).toBeInTheDocument();
  });
});
