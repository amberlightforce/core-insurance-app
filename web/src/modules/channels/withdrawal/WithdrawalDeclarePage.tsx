import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../../api/idempotency';
import { Banner, Button, KeyValueList, TextField, announce } from '../../../design-system';
import { PageHeader, Section } from '../../staff/PageHeader';
import { ProblemBanner } from '../../staff/ProblemBanner';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import {
  confirmWithdrawal,
  declareWithdrawal,
  type WithdrawalDeclareRequest,
  type WithdrawalDeclareResponse,
} from './api';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function ConfirmStep({ declared }: { declared: WithdrawalDeclareResponse }) {
  const { t } = useTranslation('channels');
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  // Demo/ops builds return the token; production sends it by e-mail only, so the customer types it.
  const [token, setToken] = useState(declared.confirmToken ?? '');
  const [tried, setTried] = useState(false);
  const mutation = useMutation({
    mutationFn: ({ key }: { key: string }) =>
      confirmWithdrawal(declared.requestNumber, token.trim(), key),
    onSuccess: () => {
      release();
      announce(t('withdrawal.declare.confirmed'));
    },
  });

  if (mutation.data?.status === 'CONFIRMED' || mutation.data?.confirmedAt) {
    return (
      <Banner variant="success" live="status" title={t('withdrawal.declare.confirmed')}>
        {t('withdrawal.declare.confirmedBody', {
          number: declared.requestNumber,
          at: mutation.data.confirmedAt ? fmt.dateTime(mutation.data.confirmedAt) : '',
        })}
      </Banner>
    );
  }
  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('withdrawal.declare.confirmTitle')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <p className={styles.muted}>{t('withdrawal.declare.confirmHelp')}</p>
      <TextField
        label={t('withdrawal.declare.token')}
        value={token}
        onChange={setToken}
        errorMessage={
          tried && token.trim() === '' ? t('withdrawal.declare.tokenRequired') : undefined
        }
      />
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('withdrawal.declare.confirmFailed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isLoading={mutation.isPending}
          onPress={() => {
            setTried(true);
            if (token.trim() === '') return;
            mutation.mutate({ key: keyFor({ n: declared.requestNumber, token: token.trim() }) });
          }}
        >
          {t('withdrawal.declare.confirm')}
        </Button>
      </div>
    </form>
  );
}

/** Customer withdrawal (distance contracts): declare, then confirm with the token (chn.Withdrawal.declare / .confirm). */
export function WithdrawalDeclarePage() {
  const { t } = useTranslation('channels');
  const fmt = useFormat();
  const { keyFor, release } = useIdempotencyKey();
  const [name, setName] = useState('');
  const [contractId, setContractId] = useState('');
  const [eMail, setEMail] = useState('');
  const [reason, setReason] = useState('');
  const [tried, setTried] = useState(false);

  const mutation = useMutation({
    mutationFn: ({ body, key }: { body: WithdrawalDeclareRequest; key: string }) =>
      declareWithdrawal(body, key),
    onSuccess: () => {
      release();
    },
  });
  const declared = mutation.data;

  const errors = {
    name: name.trim() === '' ? t('withdrawal.declare.required') : undefined,
    contractId: uuidPattern.test(contractId.trim())
      ? undefined
      : t('withdrawal.declare.badContract'),
    eMail: /^\S+@\S+\.\S+$/.test(eMail.trim()) ? undefined : t('withdrawal.declare.badEmail'),
  };

  const submit = () => {
    setTried(true);
    if (errors.name || errors.contractId || errors.eMail) return;
    const body: WithdrawalDeclareRequest = {
      name: name.trim(),
      contractId: contractId.trim(),
      eMail: eMail.trim(),
      ...(reason.trim() ? { reason: reason.trim() } : {}),
    };
    mutation.mutate({ body, key: keyFor(body) });
  };

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('withdrawal.declare.title')}
        description={t('withdrawal.declare.subtitle')}
      />
      {declared?.status === 'DECLINED' ? (
        <Banner variant="danger" live="alert" title={t('withdrawal.declare.expiredTitle')}>
          {t('withdrawal.declare.expiredBody', { number: declared.requestNumber })}
        </Banner>
      ) : declared ? (
        <Section title={t('withdrawal.declare.receipt')}>
          <KeyValueList
            aria-label={t('withdrawal.declare.receipt')}
            items={[
              {
                id: 'number',
                label: t('withdrawal.fields.requestNumber'),
                value: declared.receipt.requestNumber,
                kind: 'mono',
              },
              {
                id: 'declared',
                label: t('withdrawal.fields.declaredAt'),
                value: fmt.dateTime(declared.receipt.declaredAt),
              },
              {
                id: 'by',
                label: t('withdrawal.fields.confirmBy'),
                value: declared.receipt.confirmBy ? fmt.dateTime(declared.receipt.confirmBy) : null,
              },
            ]}
          />
          <ConfirmStep declared={declared} />
        </Section>
      ) : (
        <Section title={t('withdrawal.declare.formTitle')}>
          <form
            noValidate
            className={styles.stack}
            aria-label={t('withdrawal.declare.formTitle')}
            onSubmit={(event) => {
              event.preventDefault();
              submit();
            }}
          >
            <TextField
              label={t('withdrawal.declare.name')}
              value={name}
              onChange={setName}
              maxLength={200}
              errorMessage={tried ? errors.name : undefined}
            />
            <TextField
              label={t('withdrawal.declare.contractId')}
              helperText={t('withdrawal.declare.contractIdHelp')}
              value={contractId}
              onChange={setContractId}
              errorMessage={tried ? errors.contractId : undefined}
            />
            <TextField
              label={t('withdrawal.declare.eMail')}
              value={eMail}
              onChange={setEMail}
              maxLength={320}
              errorMessage={tried ? errors.eMail : undefined}
            />
            <TextField
              label={t('withdrawal.declare.reason')}
              multiline
              maxLength={1000}
              value={reason}
              onChange={setReason}
            />
            {mutation.isError ? (
              <ProblemBanner error={mutation.error} title={t('withdrawal.declare.failed')} />
            ) : null}
            <div className={styles.actions}>
              <Button variant="primary" isLoading={mutation.isPending} onPress={submit}>
                {t('withdrawal.declare.submit')}
              </Button>
            </div>
          </form>
        </Section>
      )}
    </div>
  );
}
