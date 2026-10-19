import { parseDate } from '@internationalized/date';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { ClaimView, PartySearchItem } from '../../../api/types';
import {
  Banner,
  Button,
  CurrencyField,
  DatePicker,
  EmptyState,
  Select,
  StatusPill,
  announce,
} from '../../../design-system';
import { compareMoney } from '../../../format';
import { PartySearchPanel } from '../../party/PartySearchPanel';
import { PartyName } from '../../reinsurance/PartyName';
import { Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { TransactionBuilder } from '../TransactionBuilder';
import {
  OPEN_STATES,
  useCreateRecovery,
  useDemandRecovery,
  useRecordMilestone,
  useRecoveries,
  useWriteOffRecovery,
  type RecoveryView,
} from './api';
import { useAccountsFor } from './useAccountsFor';

const recoveryReasons = ['THIRD_PARTY_LIABILITY', 'SALVAGE_AVAILABLE', 'OTHER'] as const;
const writeOffReasons = [
  'UNCOLLECTABLE',
  'COUNTERPARTY_INSOLVENT',
  'COST_EXCEEDS_RECOVERY',
] as const;
const milestones = [
  'COUNTERPARTY_ACKNOWLEDGED',
  'LIABILITY_ACCEPTED',
  'SETTLEMENT_AGREED',
  'SALVAGE_SOLD',
] as const;

const stateFamily: Record<string, 'info' | 'success' | 'warning' | 'disabled'> = {
  OPEN: 'info',
  DEMANDED: 'info',
  DISPUTED: 'warning',
  AGREED: 'success',
  IN_ARBITRATION: 'warning',
  IN_LITIGATION: 'warning',
  CLOSED: 'disabled',
  WRITTEN_OFF: 'disabled',
};

function RecoveryStatePill({ state }: { state: string }) {
  const { t } = useTranslation('claims');
  return (
    <StatusPill
      semantic={stateFamily[state] ?? 'info'}
      subLabel={t(`recovery.state.${state}`, { defaultValue: state })}
      announceChanges={false}
    />
  );
}

function positive(amount: string | null): amount is string {
  return amount !== null && amount !== '' && compareMoney(amount, '0.00') > 0;
}

function CreateRecoveryForm({ claim, onDone }: { claim: ClaimView; onDone: () => void }) {
  const { t } = useTranslation('claims');
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useCreateRecovery(claim.summary.claimId);
  const [type, setType] = useState<'SUBROGATION' | 'SALVAGE'>('SUBROGATION');
  const [counterparty, setCounterparty] = useState<{ id: string; name: string } | null>(null);
  const [amount, setAmount] = useState<string | null>(null);
  const [reason, setReason] = useState<string | null>(null);
  const [tried, setTried] = useState(false);

  const submit = () => {
    setTried(true);
    if (!counterparty || !positive(amount) || !reason) return;
    const money = { amount, currency: 'EUR' };
    const request = {
      claimId: claim.summary.claimId,
      type,
      counterpartyPartyId: counterparty.id,
      expectedAmount: money,
      reason,
      ...(type === 'SALVAGE'
        ? { salvage: { estimate: money, buyerPartyId: counterparty.id } }
        : {}),
    };
    mutation.mutate(
      { request, key: keyFor(request) },
      {
        onSuccess: () => {
          release();
          announce(t('recovery.created'));
          setAmount(null);
          setCounterparty(null);
          setReason(null);
          setTried(false);
          onDone();
        },
      },
    );
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('recovery.create.title')}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <div className={styles.grid}>
        <Select
          label={t('recovery.create.type')}
          isRequired
          options={(['SUBROGATION', 'SALVAGE'] as const).map((k) => ({
            id: k,
            label: t(`recovery.type.${k}`),
          }))}
          value={type}
          onChange={(value) => {
            setType(value === 'SALVAGE' ? 'SALVAGE' : 'SUBROGATION');
          }}
        />
        <CurrencyField
          label={type === 'SALVAGE' ? t('recovery.create.estimate') : t('recovery.create.expected')}
          isRequired
          name="rec-amount"
          value={amount}
          onChange={setAmount}
          {...(tried && !positive(amount)
            ? { errorMessage: t('recovery.create.errors.amount') }
            : {})}
        />
        <Select
          label={t('recovery.create.reason')}
          isRequired
          options={recoveryReasons.map((r) => ({ id: r, label: t(`recovery.reason.${r}`) }))}
          value={reason}
          onChange={setReason}
          errorMessage={tried && !reason ? t('recovery.create.errors.reason') : undefined}
        />
      </div>
      <div className={styles.stack}>
        <p className="ds-label">
          {type === 'SALVAGE' ? t('recovery.create.buyer') : t('recovery.create.counterparty')}
        </p>
        {counterparty ? (
          <p>
            <span>{counterparty.name}</span>{' '}
            <Button
              variant="secondary"
              size="sm"
              onPress={() => {
                setCounterparty(null);
              }}
            >
              {t('recovery.create.change')}
            </Button>
          </p>
        ) : (
          <PartySearchPanel
            pickOnClick
            label={t('recovery.create.search')}
            onOpen={(party: PartySearchItem) => {
              setCounterparty({ id: party.partyId, name: party.displayName });
            }}
          />
        )}
        {tried && !counterparty ? (
          <p role="alert" className={styles.muted}>
            {t('recovery.create.errors.counterparty')}
          </p>
        ) : null}
      </div>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('recovery.create.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button type="submit" variant="primary" isLoading={mutation.isPending}>
          {t('recovery.create.submit')}
        </Button>
      </div>
    </form>
  );
}

type Action = 'milestone' | 'demand' | 'writeoff' | 'reserve';

interface FormProps {
  claimId: string;
  recovery: RecoveryView;
  onDone: () => void;
}

function MilestoneForm({ claimId, recovery, onDone }: FormProps) {
  const { t } = useTranslation('claims');
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useRecordMilestone(claimId);
  const [milestone, setMilestone] = useState<string | null>(null);
  const [on, setOn] = useState('');
  const [price, setPrice] = useState<string | null>(null);
  const [tried, setTried] = useState(false);
  const sold = milestone === 'SALVAGE_SOLD';
  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('recovery.milestone.title')}
      onSubmit={(event) => {
        event.preventDefault();
        setTried(true);
        if (!milestone || !on || (sold && !positive(price))) return;
        const request = {
          recoveryId: recovery.recoveryId,
          expectedRecordVersion: recovery.recordVersion,
          milestone,
          occurredOn: on,
          ...(sold && price
            ? {
                salvage: {
                  buyerPartyId: recovery.counterpartyPartyId,
                  salePrice: { amount: price, currency: 'EUR' },
                  soldOn: on,
                },
              }
            : {}),
        };
        mutation.mutate(
          { request, key: keyFor(request) },
          {
            onSuccess: () => {
              release();
              announce(t('recovery.milestone.recorded'));
              onDone();
            },
          },
        );
      }}
    >
      <div className={styles.grid}>
        <Select
          label={t('recovery.milestone.kind')}
          isRequired
          options={milestones
            .filter((m) => m !== 'SALVAGE_SOLD' || recovery.type === 'SALVAGE')
            .map((m) => ({ id: m, label: t(`recovery.milestone.codes.${m}`) }))}
          value={milestone}
          onChange={setMilestone}
          errorMessage={tried && !milestone ? t('recovery.milestone.errors.kind') : undefined}
        />
        <DatePicker
          label={t('recovery.milestone.date')}
          isRequired
          value={on ? parseDate(on) : null}
          onChange={(value) => {
            setOn(value ? value.toString() : '');
          }}
          {...(tried && !on ? { errorMessage: t('recovery.milestone.errors.date') } : {})}
        />
        {sold ? (
          <CurrencyField
            label={t('recovery.milestone.salePrice')}
            isRequired
            name="rec-sale"
            value={price}
            onChange={setPrice}
            {...(tried && !positive(price)
              ? { errorMessage: t('recovery.milestone.errors.price') }
              : {})}
          />
        ) : null}
      </div>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('recovery.milestone.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button type="submit" variant="primary" isLoading={mutation.isPending}>
          {t('recovery.milestone.submit')}
        </Button>
      </div>
    </form>
  );
}

function DemandForm({ claimId, recovery, onDone }: FormProps) {
  const { t } = useTranslation('claims');
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useDemandRecovery(claimId);
  const [amount, setAmount] = useState<string | null>(recovery.expectedAmount.amount);
  const [due, setDue] = useState('');
  const [tried, setTried] = useState(false);
  const tooMuch = amount !== null && compareMoney(amount, recovery.expectedAmount.amount) > 0;
  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('recovery.demand.title')}
      onSubmit={(event) => {
        event.preventDefault();
        setTried(true);
        if (!positive(amount) || tooMuch || !due) return;
        const request = {
          recoveryId: recovery.recoveryId,
          expectedRecordVersion: recovery.recordVersion,
          amount: { amount, currency: 'EUR' },
          dueDate: due,
        };
        mutation.mutate(
          { request, key: keyFor(request) },
          {
            onSuccess: () => {
              release();
              announce(t('recovery.demand.done'));
            },
          },
        );
      }}
    >
      <p className={styles.muted}>{t('recovery.demand.intro')}</p>
      <div className={styles.grid}>
        <CurrencyField
          label={t('recovery.demand.amount')}
          isRequired
          name="rec-demand"
          value={amount}
          onChange={setAmount}
          {...(tried && (!positive(amount) || tooMuch)
            ? { errorMessage: t('recovery.demand.errors.amount') }
            : {})}
        />
        <DatePicker
          label={t('recovery.demand.dueDate')}
          isRequired
          value={due ? parseDate(due) : null}
          onChange={(value) => {
            setDue(value ? value.toString() : '');
          }}
          {...(tried && !due ? { errorMessage: t('recovery.demand.errors.dueDate') } : {})}
        />
      </div>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('recovery.demand.failed')} />
      ) : null}
      {mutation.isSuccess ? (
        <Banner variant="success" live="status" title={t('recovery.demand.referenceTitle')}>
          {t('recovery.demand.referenceBody')}{' '}
          <strong className="ds-mono">{mutation.data.paymentReference}</strong>
          <Button variant="secondary" size="sm" onPress={onDone}>
            {t('recovery.demand.close')}
          </Button>
        </Banner>
      ) : null}
      {mutation.isSuccess ? null : (
        <div className={styles.actions}>
          <Button type="submit" variant="primary" isLoading={mutation.isPending}>
            {t('recovery.demand.submit')}
          </Button>
        </div>
      )}
    </form>
  );
}

function WriteOffForm({ claimId, recovery, onDone }: FormProps) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useWriteOffRecovery(claimId);
  const [amount, setAmount] = useState<string | null>(null);
  const [reason, setReason] = useState<string | null>(null);
  const [tried, setTried] = useState(false);
  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('recovery.writeOff.title')}
      onSubmit={(event) => {
        event.preventDefault();
        setTried(true);
        if (!positive(amount) || !reason) return;
        const request = {
          recoveryId: recovery.recoveryId,
          expectedRecordVersion: recovery.recordVersion,
          amount: { amount, currency: 'EUR' },
          reason,
        };
        mutation.mutate(
          { request, key: keyFor(request) },
          {
            onSuccess: () => {
              release();
              announce(t('recovery.writeOff.done'));
              onDone();
            },
          },
        );
      }}
    >
      {/* TODO(hardening): the contract has no write-off dry run, so the authority outcome is only known on submit. */}
      <Banner variant="warning" live="none" title={t('recovery.writeOff.authorityTitle')}>
        {t('recovery.writeOff.authorityBody', { open: fmt.money(recovery.recoveryReserve) })}
      </Banner>
      <div className={styles.grid}>
        <CurrencyField
          label={t('recovery.writeOff.amount')}
          isRequired
          name="rec-writeoff"
          value={amount}
          onChange={setAmount}
          {...(tried && !positive(amount)
            ? { errorMessage: t('recovery.writeOff.errors.amount') }
            : {})}
        />
        <Select
          label={t('recovery.writeOff.reason')}
          isRequired
          options={writeOffReasons.map((r) => ({
            id: r,
            label: t(`recovery.writeOff.reasons.${r}`),
          }))}
          value={reason}
          onChange={setReason}
          errorMessage={tried && !reason ? t('recovery.writeOff.errors.reason') : undefined}
        />
      </div>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('recovery.writeOff.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button type="submit" variant="danger" isLoading={mutation.isPending}>
          {t('recovery.writeOff.submit')}
        </Button>
      </div>
    </form>
  );
}

function RecoveryCard({
  claim,
  recovery,
  action,
  onAction,
}: {
  claim: ClaimView;
  recovery: RecoveryView;
  action: Action | null;
  onAction: (action: Action | null) => void;
}) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const claimId = claim.summary.claimId;
  const open = claim.summary.status === 'OPEN' && OPEN_STATES.includes(recovery.state);
  const done = () => {
    onAction(null);
  };
  const buttons: [Action, string][] = [
    ['milestone', 'recovery.actions.milestone'],
    ['reserve', 'recovery.actions.reserve'],
    ...(recovery.state === 'OPEN'
      ? [['demand', 'recovery.actions.demand'] as [Action, string]]
      : []),
    ['writeoff', 'recovery.actions.writeOff'],
  ];
  return (
    <li className={styles.stack}>
      <div className={styles.tags}>
        <strong>{t(`recovery.type.${recovery.type}`)}</strong>
        <RecoveryStatePill state={recovery.state} />
        <PartyName partyId={recovery.counterpartyPartyId} />
      </div>
      <dl className={styles.grid}>
        <div>
          <dt className="ds-caption">{t('recovery.columns.expected')}</dt>
          <dd className="ds-num">{fmt.money(recovery.expectedAmount)}</dd>
        </div>
        <div>
          <dt className="ds-caption">{t('recovery.columns.received')}</dt>
          <dd className="ds-num">{fmt.money(recovery.recoveredAmount)}</dd>
        </div>
        <div>
          <dt className="ds-caption">{t('recovery.columns.reserve')}</dt>
          <dd className="ds-num">{fmt.money(recovery.recoveryReserve)}</dd>
        </div>
      </dl>
      {recovery.paymentReference ? (
        <p>
          {t('recovery.paymentReference')}{' '}
          <strong className="ds-mono">{recovery.paymentReference}</strong>
        </p>
      ) : null}
      {recovery.milestones.length > 0 ? (
        <ul aria-label={t('recovery.milestone.list')} className={styles.stack}>
          {recovery.milestones.map((m) => (
            <li key={`${m.milestone}:${m.occurredOn}:${m.recordedAt}`}>
              {t(`recovery.milestone.codes.${m.milestone}`, { defaultValue: m.milestone })} ·{' '}
              <span className="ds-caption">{fmt.date(m.occurredOn)}</span>
            </li>
          ))}
        </ul>
      ) : null}
      {open ? (
        <div className={styles.actions}>
          {buttons.map(([id, label]) => (
            <Button
              key={id}
              variant="secondary"
              size="sm"
              onPress={() => {
                onAction(action === id ? null : id);
              }}
            >
              {t(label)}
            </Button>
          ))}
        </div>
      ) : null}
      {action === 'milestone' ? (
        <MilestoneForm claimId={claimId} recovery={recovery} onDone={done} />
      ) : null}
      {action === 'demand' ? (
        <DemandForm claimId={claimId} recovery={recovery} onDone={done} />
      ) : null}
      {action === 'writeoff' ? (
        <WriteOffForm claimId={claimId} recovery={recovery} onDone={done} />
      ) : null}
    </li>
  );
}

/** SCR-CLM-08 subset: the claim's recovery cases and their actions, plus the recovery-reserve builder. */
export function RecoveriesTab({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const claimId = claim.summary.claimId;
  const query = useRecoveries(claimId);
  const accounts = useAccountsFor(claimId);
  const [active, setActive] = useState<{ id: string; action: Action } | null>(null);
  const [creating, setCreating] = useState(false);
  const open = claim.summary.status === 'OPEN';
  const fault = claim.liabilityFacts?.insuredFaultPct ?? null;

  return (
    <div className={styles.stack}>
      <QueryView query={query}>
        {(data) => {
          const items = data.items;
          // TODO(hardening): REQ-CLM-144 proposal is derived from the fault % here; use the server flag when it exists.
          const proposeSubrogation =
            open &&
            fault !== null &&
            Number(fault) < 100 &&
            !items.some((r) => r.type === 'SUBROGATION' && r.state !== 'WRITTEN_OFF');
          const openRecoveries = items.filter((r) => OPEN_STATES.includes(r.state));
          return (
            <>
              {proposeSubrogation ? (
                <Banner
                  variant="info"
                  live="none"
                  title={t('recovery.proposal.title')}
                  actions={
                    <Button
                      variant="secondary"
                      size="sm"
                      onPress={() => {
                        setCreating(true);
                      }}
                    >
                      {t('recovery.proposal.action')}
                    </Button>
                  }
                >
                  {t('recovery.proposal.body', { fault })}
                </Banner>
              ) : null}
              <Section
                title={t('recovery.title')}
                count={items.length}
                {...(open
                  ? {
                      actions: (
                        <Button
                          variant="secondary"
                          onPress={() => {
                            setCreating((v) => !v);
                          }}
                        >
                          {t('recovery.create.open')}
                        </Button>
                      ),
                    }
                  : {})}
              >
                <div className={styles.stack}>
                  {creating && open ? (
                    <CreateRecoveryForm
                      claim={claim}
                      onDone={() => {
                        setCreating(false);
                      }}
                    />
                  ) : null}
                  {items.length === 0 ? (
                    <EmptyState
                      kind="first-use"
                      headingLevel={3}
                      illustration={<></>}
                      headline={t('recovery.emptyTitle')}
                      description={t('recovery.emptyBody')}
                    />
                  ) : (
                    <ul className={styles.stack} aria-label={t('recovery.title')}>
                      {items.map((r) => (
                        <RecoveryCard
                          key={r.recoveryId}
                          claim={claim}
                          recovery={r}
                          action={active?.id === r.recoveryId ? active.action : null}
                          onAction={(action) => {
                            setActive(action ? { id: r.recoveryId, action } : null);
                          }}
                        />
                      ))}
                    </ul>
                  )}
                </div>
              </Section>
              {open && openRecoveries.length > 0 ? (
                <Section title={t('recovery.builder.title')}>
                  <TransactionBuilder
                    key={active?.action === 'reserve' ? active.id : 'recovery'}
                    claimId={claimId}
                    exposures={claim.exposures}
                    accounts={accounts}
                    recoveries={openRecoveries.map((r) => ({
                      recoveryId: r.recoveryId,
                      label: `${t(`recovery.type.${r.type}`)} · ${r.expectedAmount.amount} ${r.expectedAmount.currency}`,
                    }))}
                    initialRecoveryId={active?.action === 'reserve' ? active.id : null}
                  />
                </Section>
              ) : null}
            </>
          );
        }}
      </QueryView>
    </div>
  );
}
