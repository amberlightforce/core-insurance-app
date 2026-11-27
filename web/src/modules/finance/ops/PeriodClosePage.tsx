import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import {
  Button,
  Dialog,
  EmptyState,
  StatusPill,
  TextField,
  announce,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import { SimpleTable } from '../../staff/SimpleTable';
import styles from '../../staff/staff.module.css';
import { useCloseAction, usePeriods, type CloseAction } from './api';
import type { FinancePeriod } from './types';

/** The next close step each period state offers; every other move needs the close workflow. */
const stepsOf = (state: string): CloseAction[] =>
  state === 'OPEN' ? ['soft-close'] : state === 'SOFT_CLOSED' ? ['lock', 'reopen'] : ['reopen'];

export function PeriodStatePill({ state }: { state: string }) {
  const { t } = useTranslation('finance');
  const semantic = state === 'OPEN' ? 'success' : state === 'SOFT_CLOSED' ? 'warning' : 'locked';
  return (
    <StatusPill
      semantic={semantic}
      text={t(`ops.close.state.${state}`, { defaultValue: state })}
      announceChanges={false}
    />
  );
}

function Actions({ period, onDone }: { period: FinancePeriod; onDone: () => void }) {
  const { t } = useTranslation('finance');
  const { keyFor, release } = useIdempotencyKey();
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);
  const [pending, setPending] = useState<CloseAction | null>(null);
  const [confirm, setConfirm] = useState<CloseAction | null>(null);
  const mutation = useCloseAction(() => {
    release();
    announce(t('ops.close.done', { period: period.period }));
    setReason('');
    setTried(false);
    onDone();
  });
  const missing = reason.trim() === '';
  const start = (action: CloseAction) => {
    setTried(true);
    if (missing) return;
    // Locking and reopening change what other modules may post: ask first.
    if (action === 'soft-close') run(action);
    else setConfirm(action);
  };
  const run = (action: CloseAction) => {
    setPending(action);
    const body = { action, period: period.period, reason: reason.trim() };
    mutation.mutate({ ...body, key: keyFor(body) });
  };
  return (
    <div className={styles.stack}>
      <TextField
        label={t('ops.close.reason')}
        helperText={t('ops.close.reasonHelp')}
        multiline
        maxLength={1000}
        value={reason}
        onChange={setReason}
        errorMessage={tried && missing ? t('ops.close.reasonRequired') : undefined}
      />
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('ops.close.failed')} />
      ) : null}
      <div className={styles.actions}>
        {stepsOf(period.state).map((action) => (
          <Button
            key={action}
            variant={action === 'reopen' ? 'secondary' : 'primary'}
            isDisabled={mutation.isPending}
            isLoading={mutation.isPending && pending === action}
            onPress={() => {
              start(action);
            }}
          >
            {t(`ops.close.action.${action}`)}
          </Button>
        ))}
      </div>
      <Dialog
        title={confirm ? t(`ops.close.confirm.${confirm}.title`, { period: period.period }) : ''}
        tone="warning"
        isOpen={confirm !== null}
        onOpenChange={(open) => {
          if (!open) setConfirm(null);
        }}
        primaryAction={{
          label: confirm ? t(`ops.close.action.${confirm}`) : '',
          onAction: () => {
            if (confirm) run(confirm);
          },
        }}
      >
        <p>{confirm ? t(`ops.close.confirm.${confirm}.body`) : null}</p>
      </Dialog>
    </div>
  );
}

/** Period close (fin.Period.list, fin.Close.softClose / lock / reopen): the state of every accounting month and its next close step. */
export function PeriodClosePage() {
  const { t } = useTranslation('finance');
  const query = usePeriods();
  const [selected, setSelected] = useState<string | null>(null);
  const columns: DataColumn<FinancePeriod>[] = [
    textColumn<FinancePeriod>('period', t('ops.close.period'), (p) => p.period, { size: 140 }),
    statusColumn<FinancePeriod>(
      'state',
      t('ops.close.stateLabel'),
      (p) => p.state,
      (p) => <PeriodStatePill state={p.state} />,
    ),
    textColumn<FinancePeriod>('posting', t('ops.close.posting'), (p) =>
      p.closedForPosting ? t('ops.close.closedForPosting') : t('ops.close.openForPosting'),
    ),
  ];
  const current = query.data?.items.find((p) => p.period === selected);
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('ops.close.title')}
        overline={t('overline')}
        description={t('ops.close.lead')}
        actions={
          <LinkButton variant="secondary" to="/finance">
            {t('ops.back')}
          </LinkButton>
        }
      />
      <Section title={t('ops.close.periods')}>
        <QueryView query={query}>
          {(page) =>
            page.items.length === 0 ? (
              <EmptyState
                kind="first-use"
                illustration={<></>}
                headingLevel={3}
                headline={t('ops.close.emptyTitle')}
                description={t('ops.close.emptyBody')}
              />
            ) : (
              <SimpleTable<FinancePeriod>
                aria-label={t('ops.close.periods')}
                columns={columns}
                data={page.items}
                getRowId={(p) => p.period}
                getRowLabel={(p) => p.period}
                onOpen={(p) => {
                  setSelected(p.period);
                }}
              />
            )
          }
        </QueryView>
      </Section>
      {current ? (
        <Section title={t('ops.close.actionsFor', { period: current.period })} family="plum">
          <Actions period={current} onDone={() => undefined} />
        </Section>
      ) : null}
    </div>
  );
}
