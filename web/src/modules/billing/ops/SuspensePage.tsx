import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { InvoiceView } from '../../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  DataTable,
  EmptyState,
  KeyValueList,
  announce,
  dateColumn,
  identifierColumn,
  moneyColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { useInvoices } from '../api';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  fromCents,
  toCents,
  useAllocateSuspense,
  useSuspense,
  type AllocationAllocateRequest,
  type SuspenseListItem,
} from './api';
import { canWorkBillingOps } from './roles';

interface Candidate {
  invoice: InvoiceView;
}

function MatchPanel({ receipt }: { receipt: SuspenseListItem }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const key = useIdempotencyKey();
  const [picked, setPicked] = useState<Record<string, boolean>>({});
  const invoices = useInvoices({ billingAccountId: receipt.billingAccountId });
  const allocate = useAllocateSuspense(() => {
    key.release();
    setPicked({});
    announce(t('suspense.match.done'));
  });
  const allowed = canWorkBillingOps();

  const candidates = ((invoices.data?.items ?? []) as Candidate[]).filter(
    (i) => i.invoice.kind === 'INVOICE' && toCents(i.invoice.open.amount) > 0,
  );
  const chosen = candidates.filter((i) => picked[i.invoice.invoiceId]);
  const chosenCents = chosen.reduce((n, i) => n + toCents(i.invoice.open.amount), 0);
  const available = toCents(receipt.amount.amount);
  const over = chosenCents > available;

  return (
    <Section title={t('suspense.match.title', { number: receipt.receiptNumber })}>
      <KeyValueList
        aria-label={t('suspense.match.facts')}
        items={[
          { id: 'amount', label: t('suspense.match.amount'), value: fmt.money(receipt.amount) },
          {
            id: 'unallocated',
            label: t('suspense.match.unallocated'),
            value: fmt.money(receipt.amount),
          },
          {
            id: 'reason',
            label: t('suspense.columns.reason'),
            value: t(`suspense.reasons.${receipt.reason}`, { defaultValue: receipt.reason }),
          },
          {
            id: 'valueDate',
            label: t('suspense.columns.valueDate'),
            value: fmt.date(receipt.valueDate),
          },
        ]}
      />
      <p className={styles.muted}>{t('suspense.match.hint')}</p>
      <QueryView query={invoices}>
        {() =>
          candidates.length === 0 ? (
            <EmptyState
              kind="first-use"
              headingLevel={3}
              headline={t('suspense.match.noneTitle')}
              description={t('suspense.match.noneBody')}
            />
          ) : (
            <div className={styles.stack}>
              {candidates.map((i) => (
                <Checkbox
                  key={i.invoice.invoiceId}
                  isSelected={!!picked[i.invoice.invoiceId]}
                  isDisabled={!allowed}
                  onChange={(on) => {
                    setPicked((p) => ({ ...p, [i.invoice.invoiceId]: on }));
                  }}
                >
                  {t('suspense.match.invoice', {
                    number: i.invoice.invoiceNumber,
                    open: fmt.money(i.invoice.open),
                    due: fmt.date(i.invoice.dueDate),
                  })}
                </Checkbox>
              ))}
              {over ? (
                <Banner variant="warning" live="none" title={t('suspense.match.over')} />
              ) : null}
              {allocate.isError ? (
                <ProblemBanner error={allocate.error} title={t('suspense.match.failed')} />
              ) : null}
              <Button
                variant="primary"
                isDisabled={!allowed || chosen.length === 0 || over || allocate.isPending}
                onPress={() => {
                  const request: AllocationAllocateRequest = {
                    receiptId: receipt.receiptId,
                    lines: chosen.map((i) => ({
                      invoiceId: i.invoice.invoiceId,
                      amount: i.invoice.open,
                    })),
                  };
                  allocate.mutate({ request, key: key.keyFor(request) });
                }}
              >
                {t('suspense.match.submit', {
                  total: fmt.money({
                    amount: fromCents(chosenCents),
                    currency: receipt.amount.currency,
                  }),
                })}
              </Button>
              {!allowed ? (
                <Banner variant="info" live="none" title={t('suspense.roleNote')} />
              ) : null}
            </div>
          )
        }
      </QueryView>
    </Section>
  );
}

/** Suspense «Αδιάθετα»: unapplied receipts and the match-to-invoice workbench. */
export function SuspensePage() {
  const { t } = useTranslation('billing');
  const query = useSuspense();
  const [selected, setSelected] = useState<string | null>(null);
  const current = query.data?.items.find((r) => r.suspenseItemId === selected);

  const columns = useMemo<DataColumn<SuspenseListItem>[]>(
    () => [
      identifierColumn<SuspenseListItem>(
        'number',
        t('suspense.columns.number'),
        (r) => r.receiptNumber,
      ),
      moneyColumn<SuspenseListItem>(
        'amount',
        t('suspense.columns.amount'),
        (r) => r.amount.amount,
        {
          currency: 'EUR',
        },
      ),
      textColumn<SuspenseListItem>('reason', t('suspense.columns.reason'), (r) =>
        t(`suspense.reasons.${r.reason}`, { defaultValue: r.reason }),
      ),
      textColumn<SuspenseListItem>(
        'bankReference',
        t('suspense.columns.bankReference'),
        (r) => r.bankReference ?? '—',
      ),
      dateColumn<SuspenseListItem>(
        'valueDate',
        t('suspense.columns.valueDate'),
        (r) => r.valueDate,
      ),
    ],
    [t],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('suspense.title')}
        description={t('suspense.subtitle')}
        actions={
          <LinkButton variant="secondary" to="/billing">
            {t('refunds.backToBilling')}
          </LinkButton>
        }
      />
      <Section title={t('suspense.listTitle')} family="ochre" count={query.data?.items.length}>
        <QueryView query={query}>
          {(page) => (
            <DataTable<SuspenseListItem>
              searchable={false}
              showDensityToggle={false}
              showColumnSettings={false}
              openOnClick
              aria-label={t('suspense.listTitle')}
              columns={columns}
              data={page.items}
              getRowId={(r) => r.suspenseItemId}
              onOpen={(r) => {
                setSelected(r.suspenseItemId);
              }}
              emptyState={
                <EmptyState
                  kind="first-use"
                  headingLevel={3}
                  headline={t('suspense.emptyTitle')}
                  description={t('suspense.emptyBody')}
                />
              }
            />
          )}
        </QueryView>
      </Section>
      {current ? <MatchPanel receipt={current} /> : null}
    </div>
  );
}
