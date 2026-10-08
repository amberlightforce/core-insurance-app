import { useMutation } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { isApiError } from '../../api/client';
import { newIdempotencyKey, useIdempotencyKey } from '../../api/idempotency';
import type {
  ClaimPayeeAccountView,
  ExposureView,
  TransactionSetBuildRequest,
  TransactionSetBuildResponse,
  TransactionSetSubmitResponse,
} from '../../api/types';
import {
  Banner,
  Button,
  CurrencyField,
  ErrorSummary,
  Radio,
  RadioGroup,
  Select,
  announce,
  textColumn,
  type DataColumn,
  type ErrorSummaryItem,
} from '../../design-system';
import { compareMoney } from '../../format';
import { ProblemBanner } from '../staff/ProblemBanner';
import { rememberRecent } from '../staff/recent';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { buildSet, submitSet, useRefreshMoney } from './api';
import { costCategories, costTypes, reserveReasons, verificationKey } from './codes';
import { authorityHint } from './closeGuard';
import { SetTransactions } from './SetViews';

type Preview = NonNullable<TransactionSetBuildResponse['preview']>[number];

export interface TransactionBuilderProps {
  claimId: string;
  exposures: readonly ExposureView[];
  accounts: readonly ClaimPayeeAccountView[];
  /** Called with the id of a set that was submitted, so the set list can show it at once. */
  onSubmitted?: (setId: string) => void;
}

/**
 * Reserve change or payment builder (SCR-CLM-04 subset). «Preview» is a dry run (nothing stored): balances before
 * and after, with the lines the system adds (reserve top-up, final release) flagged. «Submit» builds the set and
 * submits it: APPROVED at once within authority, or PENDING_APPROVAL with an approval checklist (illustrative limits,
 * D-SL2-03 / D-SL2-13). The build and the submit each carry their own Idempotency-Key, reused on retry.
 */
export function TransactionBuilder({
  claimId,
  exposures,
  accounts,
  onSubmitted,
}: TransactionBuilderProps) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const buildKey = useIdempotencyKey();
  const submitKey = useIdempotencyKey();
  const refresh = useRefreshMoney(claimId);

  const open = useMemo(() => exposures.filter((e) => e.status === 'OPEN'), [exposures]);
  const [kind, setKind] = useState<'RESERVE' | 'PAYMENT'>('RESERVE');
  const [exposureId, setExposureId] = useState<string | null>(open[0]?.exposureId ?? null);
  const [costType, setCostType] = useState<string | null>('INDEMNITY');
  const [category, setCategory] = useState<string | null>('VEHICLE_REPAIR');
  const [direction, setDirection] = useState<'INCREASE' | 'DECREASE'>('INCREASE');
  const [amount, setAmount] = useState<string | null>(null);
  const [reason, setReason] = useState<string | null>(null);
  const [accountId, setAccountId] = useState<string | null>(null);
  const [paymentType, setPaymentType] = useState<'PARTIAL' | 'FINAL'>('PARTIAL');
  const [attempts, setAttempts] = useState(0);
  const [preview, setPreview] = useState<TransactionSetBuildResponse | null>(null);
  const [result, setResult] = useState<TransactionSetSubmitResponse | null>(null);

  const edited = () => {
    setPreview(null);
    setResult(null);
  };

  const errors: Record<string, string> = {};
  if (!exposureId) errors['txn-exposure'] = t('builder.errors.exposure');
  if (!costType || !category) errors['txn-category'] = t('builder.errors.category');
  if (amount === null || amount === '' || compareMoney(amount, '0.00') <= 0)
    errors['txn-amount'] = t('builder.errors.amount');
  if (kind === 'RESERVE' && !reason) errors['txn-reason'] = t('builder.errors.reason');
  if (kind === 'PAYMENT' && !accountId) errors['txn-account'] = t('builder.errors.account');
  const shown = attempts > 0 ? errors : {};
  const summary: ErrorSummaryItem[] =
    attempts > 0 ? Object.entries(errors).map(([fieldId, message]) => ({ fieldId, message })) : [];

  const request = (): TransactionSetBuildRequest | null => {
    if (Object.keys(errors).length > 0 || !exposureId || !costType || !category || !amount)
      return null;
    const account = accounts.find((a) => a.payeeAccountId === accountId);
    const signed = kind === 'RESERVE' && direction === 'DECREASE' ? `-${amount}` : amount;
    return {
      claimId,
      transactions: [
        {
          kind,
          exposureId,
          costType,
          costCategory: category,
          amount: { amount: signed, currency: 'EUR' },
          ...(kind === 'RESERVE' && reason ? { reason } : {}),
          ...(kind === 'PAYMENT' && account
            ? { payeePartyId: account.partyId, payeeAccountId: account.payeeAccountId, paymentType }
            : {}),
        },
      ],
    };
  };

  const dryRun = useMutation({
    mutationFn: (body: TransactionSetBuildRequest) => buildSet(body, newIdempotencyKey(), true),
    onSuccess: (response) => {
      setPreview(response);
      setResult(null);
    },
  });

  const submit = useMutation({
    mutationFn: async (body: TransactionSetBuildRequest) => {
      const built = await buildSet(body, buildKey.keyFor(body), false);
      return submitSet(built.setId, submitKey.keyFor({ setId: built.setId }));
    },
    onSuccess: (response) => {
      buildKey.release();
      submitKey.release();
      setResult(response);
      setPreview(null);
      setAmount(null);
      setReason(null);
      rememberRecent(`claimset.${claimId}`, { id: response.setId, label: response.status });
      onSubmitted?.(response.setId);
      announce(t(`builder.result.${response.status === 'APPROVED' ? 'approved' : 'pending'}`));
      void refresh();
    },
    onError: (error) => {
      // The balances moved under the set: it must be built again from the current state.
      if (isApiError(error) && error.code === 'CLM-ERR-SET-STALE') {
        buildKey.release();
        submitKey.release();
        setPreview(null);
        void refresh();
      }
    },
  });

  const onPreview = () => {
    setAttempts((n) => n + 1);
    const body = request();
    if (body) dryRun.mutate(body);
  };
  const onSubmit = () => {
    setAttempts((n) => n + 1);
    const body = request();
    if (body) submit.mutate(body);
  };

  const columns = useMemo<DataColumn<Preview>[]>(
    () => [
      textColumn<Preview>('line', t('builder.preview.line'), (r) => {
        const exposure = exposures.find((e) => e.exposureId === r.exposureId);
        return `${exposure?.exposureNumber ?? ''} · ${t(`codes.costType.${r.costType ?? ''}`, { defaultValue: r.costType ?? '' })} · ${t(`codes.costCategory.${r.costCategory ?? ''}`, { defaultValue: r.costCategory ?? '' })}`;
      }),
      textColumn<Preview>('open', t('builder.preview.open'), (r) =>
        r.before && r.after ? `${fmt.money(r.before)} → ${fmt.money(r.after)}` : null,
      ),
      textColumn<Preview>('paid', t('builder.preview.paid'), (r) =>
        r.paidBefore && r.paidAfter
          ? `${fmt.money(r.paidBefore)} → ${fmt.money(r.paidAfter)}`
          : null,
      ),
    ],
    [t, fmt, exposures],
  );
  const previewRows = useMemo<Preview[]>(() => preview?.preview ?? [], [preview]);

  const hint = authorityHint(preview?.checks) ?? authorityHint(result?.authorityChecks);
  const error = submit.isError ? submit.error : dryRun.isError ? dryRun.error : null;
  const busy = dryRun.isPending || submit.isPending;

  const categories = costType ? (costCategories[costType] ?? []) : [];

  if (open.length === 0) return <p className={styles.muted}>{t('builder.noOpenExposure')}</p>;

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('builder.title')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <ErrorSummary errors={summary} focusKey={attempts} />
      <RadioGroup
        label={t('builder.kind')}
        orientation="horizontal"
        value={kind}
        onChange={(value) => {
          setKind(value === 'PAYMENT' ? 'PAYMENT' : 'RESERVE');
          edited();
        }}
      >
        <Radio value="RESERVE">{t('builder.kinds.RESERVE')}</Radio>
        <Radio value="PAYMENT">{t('builder.kinds.PAYMENT')}</Radio>
      </RadioGroup>
      <div className={styles.grid}>
        <Select
          id="txn-exposure"
          label={t('builder.exposure')}
          isRequired
          options={open.map((e) => ({
            id: e.exposureId,
            label: `${e.exposureNumber} · ${t(`exposure.kinds.${e.kind}`)} · ${e.coverageCode}`,
          }))}
          value={exposureId}
          onChange={(value) => {
            setExposureId(value);
            edited();
          }}
          errorMessage={shown['txn-exposure']}
        />
        <Select
          label={t('builder.costType')}
          isRequired
          helperText={t('builder.illustrative')}
          options={costTypes.map((c) => ({ id: c, label: t(`codes.costType.${c}`) }))}
          value={costType}
          onChange={(value) => {
            setCostType(value);
            setCategory(value ? (costCategories[value]?.[0] ?? null) : null);
            edited();
          }}
        />
        <Select
          id="txn-category"
          label={t('builder.costCategory')}
          isRequired
          options={categories.map((c) => ({ id: c, label: t(`codes.costCategory.${c}`) }))}
          value={category}
          onChange={(value) => {
            setCategory(value);
            edited();
          }}
          errorMessage={shown['txn-category']}
        />
        {kind === 'RESERVE' ? (
          <RadioGroup
            label={t('builder.direction')}
            orientation="horizontal"
            value={direction}
            onChange={(value) => {
              setDirection(value === 'DECREASE' ? 'DECREASE' : 'INCREASE');
              edited();
            }}
          >
            <Radio value="INCREASE">{t('builder.directions.INCREASE')}</Radio>
            <Radio value="DECREASE">{t('builder.directions.DECREASE')}</Radio>
          </RadioGroup>
        ) : null}
        <CurrencyField
          label={kind === 'RESERVE' ? t('builder.reserveAmount') : t('builder.paymentAmount')}
          isRequired
          name="txn-amount"
          value={amount}
          onChange={(value) => {
            setAmount(value);
            edited();
          }}
          {...(shown['txn-amount'] ? { errorMessage: shown['txn-amount'] } : {})}
        />
        {kind === 'RESERVE' ? (
          <Select
            id="txn-reason"
            label={t('builder.reason')}
            isRequired
            helperText={t('builder.illustrative')}
            options={reserveReasons.map((r) => ({ id: r, label: t(`codes.reserveReason.${r}`) }))}
            value={reason}
            onChange={(value) => {
              setReason(value);
              edited();
            }}
            errorMessage={shown['txn-reason']}
          />
        ) : (
          <Select
            id="txn-account"
            label={t('builder.account')}
            isRequired
            {...(accounts.length === 0 ? { helperText: t('builder.noAccounts') } : {})}
            options={accounts.map((a) => ({
              id: a.payeeAccountId,
              label: `${a.maskedIban} · ${t(`payee.verificationStatus.${verificationKey(a.verificationStatus)}`, { defaultValue: a.verificationStatus })}`,
            }))}
            value={accountId}
            onChange={(value) => {
              setAccountId(value);
              edited();
            }}
            errorMessage={shown['txn-account']}
          />
        )}
      </div>
      {kind === 'PAYMENT' ? (
        <>
          <RadioGroup
            label={t('builder.paymentType')}
            orientation="horizontal"
            value={paymentType}
            onChange={(value) => {
              setPaymentType(value === 'FINAL' ? 'FINAL' : 'PARTIAL');
              edited();
            }}
          >
            <Radio value="PARTIAL">{t('builder.paymentTypes.PARTIAL')}</Radio>
            <Radio value="FINAL">{t('builder.paymentTypes.FINAL')}</Radio>
          </RadioGroup>
          {paymentType === 'FINAL' ? (
            <Banner variant="info" live="none" title={t('builder.final.title')}>
              {t('builder.final.body')}
            </Banner>
          ) : null}
        </>
      ) : null}

      {preview ? (
        <div className={styles.stack}>
          <p className="ds-label">{t('builder.preview.title')}</p>
          <SimpleTable<Preview>
            aria-label={t('builder.preview.title')}
            columns={columns}
            data={previewRows}
            getRowId={(r) => `${r.exposureId ?? ''}:${r.costType ?? ''}:${r.costCategory ?? ''}`}
          />
          {preview.set?.transactions.some((x) => x.proposed) ? (
            <Banner variant="info" live="none" title={t('builder.preview.proposedTitle')}>
              {t('builder.preview.proposedBody')}
              <SetTransactions
                transactions={preview.set.transactions}
                label={t('builder.preview.transactions')}
              />
            </Banner>
          ) : null}
        </div>
      ) : null}

      {hint ? (
        <Banner
          variant={hint === 'within' ? 'success' : 'warning'}
          live="none"
          title={t(`builder.hint.${hint}.title`)}
        >
          {t(`builder.hint.${hint}.body`)}
        </Banner>
      ) : null}

      {error ? <ProblemBanner error={error} title={t('builder.failed')} /> : null}

      {result ? (
        <div className={styles.stack}>
          <Banner
            variant={result.status === 'APPROVED' ? 'success' : 'warning'}
            live="status"
            title={t(`builder.result.${result.status === 'APPROVED' ? 'approved' : 'pending'}`)}
          >
            {t(`builder.result.${result.status === 'APPROVED' ? 'approvedBody' : 'pendingBody'}`)}
            {result.status === 'APPROVED' && result.payments && result.payments.length > 0
              ? ` ${t('builder.result.paymentSent')}`
              : ''}
          </Banner>
          {result.status !== 'APPROVED' && !result.set?.approvals?.length ? (
            <ul className={styles.stack} aria-label={t('builder.result.referrals')}>
              {(result.authorityChecks ?? [])
                .filter((c) => c.decision === 'REFER')
                .map((c, index) => (
                  <li key={`${c.type ?? ''}:${c.costType ?? ''}:${String(index)}`}>
                    {t('builder.result.referral', {
                      role: c.referralRole ?? '—',
                      amount: c.amount ? fmt.money(c.amount) : '—',
                    })}
                  </li>
                ))}
            </ul>
          ) : null}
        </div>
      ) : null}

      <div className={styles.actions}>
        <Button
          variant="secondary"
          isLoading={dryRun.isPending}
          isDisabled={busy}
          onPress={onPreview}
        >
          {t('builder.preview.action')}
        </Button>
        <Button variant="primary" isLoading={submit.isPending} isDisabled={busy} onPress={onSubmit}>
          {t(kind === 'RESERVE' ? 'builder.submitReserve' : 'builder.submitPayment')}
        </Button>
      </div>
    </form>
  );
}
