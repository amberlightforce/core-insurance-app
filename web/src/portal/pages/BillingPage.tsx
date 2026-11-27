import { useInfiniteQuery, useMutation, useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import {
  Banner,
  Button,
  EmptyState,
  LoadingState,
  SkeletonText,
  StatusPill,
  announce,
} from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { QueryView } from '../ui/QueryView';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import { norm, useCodeLabel } from '../ui/labels';
import rows from '../ui/pages.module.css';
import {
  getBillingSummary,
  listInvoices,
  listRefunds,
  payNow,
  portalKeys,
  type PortalInvoice,
  type PortalPayNow,
} from './portalApi';

function Summary() {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const query = useQuery({
    queryKey: [...portalKeys.billing, 'summary'],
    queryFn: getBillingSummary,
  });
  return (
    <QueryView query={query} errorTitle={t('pay.loadFailed')}>
      {(summary) =>
        summary.accounts.length === 0 ? null : (
          <>
            {summary.accounts.map((a) => (
              <PortalSection
                key={a.billingAccountId}
                title={t('pay.account', { number: a.accountNumber })}
                family="brand"
              >
                <ul className={rows.figures}>
                  <li className={rows.figure}>
                    <p className={rows.figureValue}>{fmt.money(a.billed)}</p>
                    <p className={rows.figureLabel}>{t('pay.billed')}</p>
                  </li>
                  <li className={rows.figure}>
                    <p className={rows.figureValue}>{fmt.money(a.overdue)}</p>
                    <p className={rows.figureLabel}>{t('pay.overdueAmount')}</p>
                  </li>
                  <li className={rows.figure}>
                    <p className={rows.figureValue}>{fmt.money(a.credit)}</p>
                    <p className={rows.figureLabel}>{t('pay.credit')}</p>
                  </li>
                </ul>
              </PortalSection>
            ))}
          </>
        )
      }
    </QueryView>
  );
}

function PayNowResult({ result }: { result: PortalPayNow }) {
  const { t } = useTranslation('portal');
  // There is no payment page yet: whatever the decision, never pretend that a payment happened or open a pay URL.
  if (result.decision === 'DENY') {
    return (
      <Banner variant="warning" live="status" title={t('pay.denied')}>
        {t('pay.deniedBody')}
      </Banner>
    );
  }
  if (result.decision === 'REFER') {
    return (
      <Banner variant="info" live="status" title={t('pay.referred')}>
        {t('pay.referredBody')}
      </Banner>
    );
  }
  return (
    <Banner variant="info" live="status" title={t('pay.soon')}>
      {t('pay.soonBody')}
    </Banner>
  );
}

function InvoiceRow({ invoice }: { invoice: PortalInvoice }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const label = useCodeLabel();
  const { keyFor, release } = useIdempotencyKey();
  const pay = useMutation({
    mutationFn: () => payNow(invoice.invoiceId, keyFor(invoice.invoiceId)),
    onSuccess: () => {
      release();
      announce(t('pay.soon'));
    },
  });
  const state = norm(invoice.state);
  const paid = state === 'paid';
  return (
    <li className={rows.row}>
      <div className={rows.rowMain}>
        <p className={rows.rowTitle}>
          {t('pay.invoice')} <span className={rows.mono}>{invoice.invoiceNumber}</span>
        </p>
        <p className={rows.rowMeta}>
          {t('pay.dates', { issued: fmt.date(invoice.issueDate), due: fmt.date(invoice.dueDate) })}
          {invoice.policyNumber ? ` · ${invoice.policyNumber}` : ''}
        </p>
      </div>
      <div className={rows.rowSide}>
        <p className={rows.amount}>{fmt.money(paid ? invoice.total : invoice.open)}</p>
        {invoice.overdue && !paid ? (
          <StatusPill semantic="overdue" text={t('pay.overdue')} />
        ) : (
          <StatusPill
            semantic={paid ? 'success' : 'info'}
            text={label('invoiceState', invoice.state)}
          />
        )}
        {invoice.payable && !pay.isSuccess ? (
          <Button
            variant="primary"
            size="sm"
            isLoading={pay.isPending}
            onPress={() => {
              pay.mutate();
            }}
          >
            {t('pay.payNow')}
          </Button>
        ) : null}
      </div>
      {pay.isError ? (
        <div className={rows.fullRow}>
          <PortalProblem error={pay.error} title={t('pay.payFailed')} />
        </div>
      ) : null}
      {pay.data ? (
        <div className={rows.fullRow}>
          <PayNowResult result={pay.data} />
        </div>
      ) : null}
    </li>
  );
}

function Invoices() {
  const { t } = useTranslation('portal');
  const query = useInfiniteQuery({
    queryKey: [...portalKeys.billing, 'invoices'],
    queryFn: ({ pageParam }) => listInvoices(pageParam),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <PortalSection title={t('pay.invoices')} family="warning">
      {query.isPending ? (
        <LoadingState immediate>
          <SkeletonText lines={3} />
        </LoadingState>
      ) : query.isError ? (
        <PortalProblem
          error={query.error}
          title={t('pay.loadFailed')}
          onRetry={() => {
            void query.refetch();
          }}
        />
      ) : items.length === 0 ? (
        <EmptyState
          kind="done"
          headingLevel={3}
          headline={t('pay.noInvoices')}
          description={t('pay.noInvoicesBody')}
        />
      ) : (
        <>
          <ul className={rows.rows}>
            {items.map((i) => (
              <InvoiceRow key={i.invoiceId} invoice={i} />
            ))}
          </ul>
          {query.hasNextPage ? (
            <div>
              <Button
                variant="secondary"
                isLoading={query.isFetchingNextPage}
                onPress={() => {
                  void query.fetchNextPage();
                }}
              >
                {t('states.loadMore')}
              </Button>
            </div>
          ) : null}
        </>
      )}
    </PortalSection>
  );
}

function Refunds() {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const label = useCodeLabel();
  const query = useQuery({ queryKey: [...portalKeys.billing, 'refunds'], queryFn: listRefunds });
  return (
    <PortalSection title={t('pay.refunds')} family="success">
      <QueryView query={query} errorTitle={t('pay.loadFailed')}>
        {(data) =>
          data.items.length === 0 ? (
            <p className={rows.rowMeta}>{t('pay.noRefunds')}</p>
          ) : (
            <ul className={rows.rows}>
              {data.items.map((r) => (
                <li key={r.refundId} className={rows.row}>
                  <div className={rows.rowMain}>
                    <p className={rows.rowTitle}>{fmt.money(r.amount)}</p>
                    <p className={rows.rowMeta}>
                      {t('pay.proposed', { date: fmt.date(r.proposedAt) })}
                    </p>
                  </div>
                  <StatusPill semantic="info" text={label('refundState', r.state)} />
                </li>
              ))}
            </ul>
          )
        }
      </QueryView>
    </PortalSection>
  );
}

/** `/portal/billing`: balances, invoices (with the pay-now request) and refunds. */
export function BillingPage() {
  const { t } = useTranslation('portal');
  usePortalTitle(t('nav.billing'));
  return (
    <PortalPage>
      <PortalHero overline={t('pay.overline')} title={t('pay.title')} lead={t('pay.lead')} />
      <Summary />
      <Invoices />
      <Refunds />
    </PortalPage>
  );
}
