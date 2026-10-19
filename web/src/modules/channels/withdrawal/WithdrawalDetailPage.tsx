import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Banner, Button, KeyValueList, announce } from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { useCloseWithdrawal, useWithdrawal, type WithdrawalGetResponse } from './api';
import { WithdrawalStatusPill } from './parts';

function CloseBar({ id }: { id: string }) {
  const { t } = useTranslation('channels');
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useCloseWithdrawal();
  const close = (outcome: 'PROCESSED' | 'REJECTED') => {
    mutation.mutate(
      { id, outcome, key: keyFor({ id, outcome }) },
      {
        onSuccess: () => {
          release();
          announce(t('withdrawal.detail.closed'));
        },
      },
    );
  };
  return (
    <div className={styles.stack}>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('withdrawal.detail.closeFailed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isLoading={mutation.isPending}
          onPress={() => {
            close('PROCESSED');
          }}
        >
          {t('withdrawal.detail.markProcessed')}
        </Button>
        <Button
          variant="danger"
          isDisabled={mutation.isPending}
          onPress={() => {
            close('REJECTED');
          }}
        >
          {t('withdrawal.detail.markRejected')}
        </Button>
      </div>
    </div>
  );
}

function Details({ data }: { data: WithdrawalGetResponse }) {
  const { t } = useTranslation('channels');
  const fmt = useFormat();
  const date = (v?: string) => (v ? fmt.dateTime(v) : null);
  const open = data.status === 'CONFIRMED' || data.status === 'SUBMITTED';
  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('withdrawal.detail.title', { number: data.requestNumber })}
        subtitle={<WithdrawalStatusPill status={data.status} />}
        actions={
          <>
            {data.policyId ? (
              <LinkButton variant="secondary" to={`/policies/${data.policyId}`}>
                {t('withdrawal.detail.openPolicy')}
              </LinkButton>
            ) : null}
            <LinkButton variant="secondary" to="/channels/withdrawals">
              {t('withdrawal.detail.back')}
            </LinkButton>
          </>
        }
      />
      {data.status === 'DECLINED' ? (
        <Banner variant="danger" live="none" title={t('withdrawal.declare.expiredTitle')}>
          {data.declineCode ?? 'CHN-ERR-RIGHT-EXPIRED'}
        </Banner>
      ) : null}
      <Section title={t('withdrawal.detail.summary')}>
        <KeyValueList
          aria-label={t('withdrawal.detail.summary')}
          items={[
            {
              id: 'number',
              label: t('withdrawal.fields.requestNumber'),
              value: data.requestNumber,
              kind: 'mono',
            },
            {
              id: 'policy',
              label: t('withdrawal.fields.policy'),
              value: data.policyId ?? null,
              kind: 'mono',
            },
            {
              id: 'refund',
              label: t('withdrawal.fields.refundStatus'),
              value: data.refundStatus ?? null,
            },
            {
              id: 'declared',
              label: t('withdrawal.fields.declaredAt'),
              value: date(data.declaredAt),
            },
            {
              id: 'confirmed',
              label: t('withdrawal.fields.confirmedAt'),
              value: date(data.confirmedAt),
            },
            {
              id: 'deadline',
              label: t('withdrawal.fields.deadlineAt'),
              value: date(data.deadlineAt),
            },
            { id: 'channel', label: t('withdrawal.fields.channel'), value: data.channel ?? null },
          ]}
        />
      </Section>
      {open ? (
        <Section title={t('withdrawal.detail.outcome')}>
          <CloseBar id={data.withdrawalRequestId} />
        </Section>
      ) : null}
    </div>
  );
}

/** One withdrawal request with its status, refund status and the staff outcome bar (chn.Withdrawal.get / .close). */
export function WithdrawalDetailPage() {
  const { t } = useTranslation('channels');
  const { id = '' } = useParams();
  const query = useWithdrawal(id);
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('withdrawal.detail.notFound')}>
        {(data) => <Details data={data} />}
      </QueryView>
    </div>
  );
}
