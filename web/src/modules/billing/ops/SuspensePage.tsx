import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { InvoiceView } from '../../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  DataTable,
  Dialog,
  EmptyState,
  KeyValueList,
  Select,
  StatusPill,
  TextField,
  announce,
  dateColumn,
  identifierColumn,
  moneyColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { useInvoices } from '../api';
import { RefundPayeeForm } from '../refunds/RefundPayeeForm';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  fromCents,
  toCents,
  suspenseStates,
  useAccountLookup,
  useAllocateSuspense,
  useRefundSuspense,
  useSuggestSuspense,
  useSuspense,
  useSuspenseItem,
  useTransferSuspense,
  type AllocationAllocateRequest,
  type SuspenseListItem,
  type SuspenseRefundToSenderResponse,
  type SuspenseState,
} from './api';
import { canMoveSuspense, canViewSuspense, canWorkBillingOps } from './roles';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const refundReasons = ['OVERPAYMENT', 'CREDIT_BALANCE', 'OTHER'] as const;

const pillSemantic = {
  OPEN: 'warning',
  REFUND_PENDING: 'pending-approval',
  ALLOCATED: 'success',
  REFUNDED: 'info',
} as const satisfies Record<SuspenseState, string>;

function StatePill({ state }: { state: SuspenseState }) {
  const { t } = useTranslation('billing');
  return (
    <StatusPill
      semantic={pillSemantic[state]}
      text={t(`suspense.states.${state}`, { defaultValue: state })}
      announceChanges={false}
    />
  );
}

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

function SuggestPanel({ item }: { item: SuspenseListItem }) {
  const { t } = useTranslation('billing');
  const key = useIdempotencyKey();
  const suggest = useSuggestSuspense();
  const result = suggest.data;
  return (
    <Section title={t('suspense.suggest.title')} family="plum">
      <p className={styles.muted}>{t('suspense.suggest.hint')}</p>
      <div className={styles.actions}>
        <Button
          variant="secondary"
          isLoading={suggest.isPending}
          onPress={() => {
            suggest.mutate({
              suspenseItemId: item.suspenseItemId,
              key: key.keyFor({ id: item.suspenseItemId }),
            });
          }}
        >
          {t('suspense.suggest.run')}
        </Button>
      </div>
      {suggest.isError ? (
        <ProblemBanner error={suggest.error} title={t('suspense.suggest.failed')} />
      ) : null}
      {result ? (
        result.suggestions.length === 0 ? (
          <EmptyState
            kind="first-use"
            headingLevel={3}
            headline={t('suspense.suggest.noneTitle')}
            description={result.note ?? t('suspense.suggest.noneBody')}
          />
        ) : (
          <ol className={styles.stack} aria-label={t('suspense.suggest.title')}>
            {result.suggestions.map((s, index) => (
              <li key={s.invoiceNo}>
                <strong>
                  {t('suspense.suggest.rank', { rank: index + 1, invoice: s.invoiceNo })}
                </strong>{' '}
                <span className={styles.muted}>
                  {s.exactAmount
                    ? t('suspense.suggest.exact', { score: s.score.toFixed(2) })
                    : t('suspense.suggest.partial', { score: s.score.toFixed(2) })}
                </span>
              </li>
            ))}
          </ol>
        )
      ) : null}
    </Section>
  );
}

function TransferPanel({ item }: { item: SuspenseListItem }) {
  const { t } = useTranslation('billing');
  const key = useIdempotencyKey();
  const [target, setTarget] = useState('');
  const [comment, setComment] = useState('');
  const [confirming, setConfirming] = useState(false);
  const [done, setDone] = useState(false);
  const targetId = target.trim();
  const valid = uuid.test(targetId);
  const lookup = useAccountLookup(valid ? targetId : null);
  const account = lookup.data;
  const same = valid && targetId.toLowerCase() === item.billingAccountId.toLowerCase();
  const currencyOk = !account || account.currency === item.amount.currency;
  const active = !account || account.status === 'ACTIVE';
  const ready = valid && !!account && !same && currencyOk && active;
  const transfer = useTransferSuspense(() => {
    key.release();
    setConfirming(false);
    setTarget('');
    setComment('');
    setDone(true);
    announce(t('suspense.transfer.done'));
  });
  const request = {
    suspenseItemId: item.suspenseItemId,
    targetBillingAccountId: targetId,
    ...(comment.trim() ? { comment: comment.trim() } : {}),
  };
  const error = (() => {
    if (target === '') return undefined;
    if (!valid) return t('suspense.transfer.invalid');
    if (same) return t('suspense.transfer.same');
    return undefined;
  })();
  return (
    <Section title={t('suspense.transfer.title')} family="ochre">
      <p className={styles.muted}>{t('suspense.transfer.hint')}</p>
      <div className={styles.grid}>
        <TextField
          label={t('suspense.transfer.target')}
          helperText={t('suspense.transfer.targetHelp')}
          autoComplete="off"
          value={target}
          onChange={(v) => {
            setTarget(v);
            setDone(false);
          }}
          errorMessage={error}
        />
        <TextField
          label={t('suspense.transfer.comment')}
          maxLength={500}
          value={comment}
          onChange={setComment}
        />
      </div>
      {valid && lookup.isError ? (
        <ProblemBanner error={lookup.error} title={t('suspense.transfer.lookupFailed')} />
      ) : null}
      {account ? (
        <KeyValueList
          aria-label={t('suspense.transfer.accountFacts')}
          items={[
            {
              id: 'no',
              label: t('suspense.transfer.accountNumber'),
              value: account.accountNumber,
              kind: 'mono',
            },
            { id: 'cur', label: t('suspense.transfer.currency'), value: account.currency },
            {
              id: 'st',
              label: t('suspense.transfer.accountStatus'),
              value: t(`suspense.transfer.status.${account.status}`, {
                defaultValue: account.status,
              }),
            },
          ]}
        />
      ) : null}
      {account && !currencyOk ? (
        <Banner variant="warning" live="none" title={t('suspense.transfer.currencyMismatch')} />
      ) : null}
      {account && !active ? (
        <Banner variant="warning" live="none" title={t('suspense.transfer.notActive')} />
      ) : null}
      {done ? <Banner variant="success" live="status" title={t('suspense.transfer.done')} /> : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isDisabled={!ready}
          onPress={() => {
            setConfirming(true);
          }}
        >
          {t('suspense.transfer.submit')}
        </Button>
      </div>
      <Dialog
        title={t('suspense.transfer.confirmTitle')}
        tone="warning"
        isOpen={confirming}
        onOpenChange={setConfirming}
        primaryAction={{
          label: t('suspense.transfer.confirm'),
          onAction: () => transfer.mutateAsync({ request, key: key.keyFor(request) }),
        }}
      >
        <p>
          {t('suspense.transfer.confirmBody', {
            number: item.receiptNumber,
            account: account?.accountNumber ?? targetId,
          })}
        </p>
        {transfer.isError ? (
          <ProblemBanner error={transfer.error} title={t('suspense.transfer.failed')} />
        ) : null}
      </Dialog>
    </Section>
  );
}

function RefundPanel({ item }: { item: SuspenseListItem }) {
  const { t } = useTranslation('billing');
  const key = useIdempotencyKey();
  const navigate = useNavigate();
  const owner = useAccountLookup(item.billingAccountId);
  const [payeeId, setPayeeId] = useState('');
  const [reason, setReason] = useState<string | null>(null);
  const [comment, setComment] = useState('');
  const [tried, setTried] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [created, setCreated] = useState<SuspenseRefundToSenderResponse | null>(null);
  const payee = payeeId.trim();
  const payeeValid = uuid.test(payee);
  const refund = useRefundSuspense((response) => {
    key.release();
    setConfirming(false);
    setCreated(response);
    announce(t('suspense.refund.done'));
  });
  const reasonOptions = useMemo(
    () => refundReasons.map((id) => ({ id, label: t(`refunds.propose.reasons.${id}`) })),
    [t],
  );
  const request =
    reason !== null && payeeValid
      ? {
          suspenseItemId: item.suspenseItemId,
          payeeAccountId: payee,
          reasonCode: reason,
          ...(comment.trim() ? { comment: comment.trim() } : {}),
        }
      : null;

  if (created) {
    return (
      <Section title={t('suspense.refund.title')} family="teal">
        <Banner variant="success" live="status" title={t('suspense.refund.done')}>
          <p>{t('suspense.refund.approvalNote')}</p>
          <div className={styles.actions}>
            <Button
              variant="primary"
              onPress={() => {
                void navigate(`/billing/refunds/${created.refund.refundId}`);
              }}
            >
              {t('suspense.refund.open')}
            </Button>
          </div>
        </Banner>
      </Section>
    );
  }
  return (
    <Section title={t('suspense.refund.title')} family="teal">
      <p className={styles.muted}>{t('suspense.refund.hint')}</p>
      {owner.data ? (
        <RefundPayeeForm
          partyId={owner.data.payerPartyId}
          onSaved={(account) => {
            setPayeeId(account.payeeAccountId);
          }}
        />
      ) : owner.isError ? (
        <ProblemBanner error={owner.error} title={t('suspense.refund.accountFailed')} />
      ) : null}
      <div className={styles.grid}>
        <TextField
          label={t('suspense.refund.payee')}
          helperText={t('suspense.refund.payeeHelp')}
          autoComplete="off"
          value={payeeId}
          onChange={setPayeeId}
          errorMessage={tried && !payeeValid ? t('suspense.refund.payeeRequired') : undefined}
        />
        <Select
          label={t('suspense.refund.reason')}
          isRequired
          placeholder={t('refunds.propose.reasonPlaceholder')}
          options={reasonOptions}
          value={reason}
          onChange={setReason}
          errorMessage={tried && reason === null ? t('refunds.propose.reasonRequired') : undefined}
        />
        <TextField
          label={t('suspense.transfer.comment')}
          maxLength={500}
          value={comment}
          onChange={setComment}
        />
      </div>
      <div className={styles.actions}>
        <Button
          variant="primary"
          onPress={() => {
            setTried(true);
            if (request) setConfirming(true);
          }}
        >
          {t('suspense.refund.submit')}
        </Button>
      </div>
      <Dialog
        title={t('suspense.refund.confirmTitle')}
        tone="warning"
        isOpen={confirming}
        onOpenChange={setConfirming}
        primaryAction={{
          label: t('suspense.refund.confirm'),
          onAction: () =>
            request ? refund.mutateAsync({ request, key: key.keyFor(request) }) : undefined,
        }}
      >
        <p>{t('suspense.refund.confirmBody', { number: item.receiptNumber })}</p>
        {refund.isError ? (
          <ProblemBanner error={refund.error} title={t('suspense.refund.failed')} />
        ) : null}
      </Dialog>
    </Section>
  );
}

function SuspenseDetail({ id }: { id: string }) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const query = useSuspenseItem(id);
  const item = query.data;
  const account = useAccountLookup(item?.billingAccountId ?? null);
  return (
    <QueryView query={query}>
      {() =>
        item ? (
          <>
            <Section
              title={t('suspense.detail.title', { number: item.receiptNumber })}
              family="brand"
              actions={<StatePill state={item.state} />}
            >
              <KeyValueList
                aria-label={t('suspense.match.facts')}
                items={[
                  {
                    id: 'receipt',
                    label: t('suspense.columns.number'),
                    value: item.receiptNumber,
                    kind: 'mono',
                  },
                  {
                    id: 'amount',
                    label: t('suspense.match.amount'),
                    value: fmt.money(item.amount),
                    kind: 'money',
                  },
                  {
                    id: 'state',
                    label: t('suspense.detail.state'),
                    value: t(`suspense.states.${item.state}`, { defaultValue: item.state }),
                  },
                  {
                    id: 'reason',
                    label: t('suspense.columns.reason'),
                    value: t(`suspense.reasons.${item.reason}`, { defaultValue: item.reason }),
                  },
                  {
                    id: 'bank',
                    label: t('suspense.columns.bankReference'),
                    value: item.bankReference ?? '—',
                    kind: 'mono',
                  },
                  {
                    id: 'payer',
                    label: t('suspense.detail.payer'),
                    value: account.data
                      ? `${account.data.accountNumber} · ${account.data.payerPartyId}`
                      : '—',
                    kind: 'mono',
                  },
                  {
                    id: 'valueDate',
                    label: t('suspense.columns.valueDate'),
                    value: fmt.date(item.valueDate),
                  },
                  {
                    id: 'recorded',
                    label: t('suspense.detail.recorded'),
                    value: fmt.dateTime(item.recordedAt),
                  },
                ]}
              />
              <div className={styles.actions}>
                <LinkButton variant="secondary" to={`/billing/accounts/${item.billingAccountId}`}>
                  {t('suspense.detail.account')}
                </LinkButton>
                {item.refundId ? (
                  <LinkButton variant="secondary" to={`/billing/refunds/${item.refundId}`}>
                    {t('suspense.detail.refund')}
                  </LinkButton>
                ) : null}
              </div>
            </Section>
            {item.state === 'OPEN' ? (
              <>
                {canViewSuspense() ? <SuggestPanel item={item} /> : null}
                <MatchPanel receipt={item} />
                {canMoveSuspense() ? (
                  <>
                    <TransferPanel item={item} />
                    <RefundPanel item={item} />
                  </>
                ) : (
                  <Banner variant="info" live="none" title={t('suspense.moveRoleNote')} />
                )}
              </>
            ) : (
              <Banner variant="info" live="none" title={t(`suspense.closedNote.${item.state}`)} />
            )}
          </>
        ) : (
          <EmptyState
            kind="first-use"
            headingLevel={3}
            headline={t('suspense.detail.notFoundTitle')}
            description={t('suspense.detail.notFoundBody')}
          />
        )
      }
    </QueryView>
  );
}

/** Suspense «Αδιάθετα»: unapplied receipts and the match-to-invoice workbench. */
export function SuspensePage() {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const { suspenseItemId } = useParams();
  const [state, setState] = useState<SuspenseState>('OPEN');
  const query = useSuspense(state);

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
      textColumn<SuspenseListItem>('state', t('suspense.detail.state'), (r) =>
        t(`suspense.states.${r.state}`, { defaultValue: r.state }),
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
        <Select
          label={t('suspense.filter')}
          options={suspenseStates.map((id) => ({ id, label: t(`suspense.states.${id}`) }))}
          value={state}
          onChange={(v) => {
            if (v) setState(v as SuspenseState);
          }}
        />
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
                void navigate(`/billing/suspense/${r.suspenseItemId}`);
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
      {suspenseItemId ? <SuspenseDetail id={suspenseItemId} /> : null}
    </div>
  );
}
