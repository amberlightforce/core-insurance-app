import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import { Banner, Button, KeyValueList, TextField, announce } from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import styles from '../ui/portal.module.css';
import {
  confirmWithdrawal,
  declareWithdrawal,
  type WithdrawalDeclareRequest,
  type WithdrawalDeclareResponse,
} from './withdrawalApi';

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function ConfirmStep({ declared }: { declared: WithdrawalDeclareResponse }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const { keyFor, release } = useIdempotencyKey();
  // Demo/ops builds return the token; production sends it by e-mail only, so the customer types it.
  const [token, setToken] = useState(declared.confirmToken ?? '');
  const [tried, setTried] = useState(false);
  const mutation = useMutation({
    mutationFn: ({ key }: { key: string }) =>
      confirmWithdrawal(declared.requestNumber, token.trim(), key),
    onSuccess: () => {
      release();
      announce(t('withdrawal.confirmed'));
    },
  });

  if (mutation.data?.status === 'CONFIRMED' || mutation.data?.confirmedAt) {
    return (
      <Banner variant="success" live="status" title={t('withdrawal.confirmed')}>
        {t('withdrawal.confirmedBody', {
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
      aria-label={t('withdrawal.confirmTitle')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      <p className={styles.muted}>{t('withdrawal.confirmHelp')}</p>
      <TextField
        label={t('withdrawal.token')}
        value={token}
        onChange={setToken}
        errorMessage={tried && token.trim() === '' ? t('withdrawal.tokenRequired') : undefined}
      />
      {mutation.isError ? (
        <PortalProblem error={mutation.error} title={t('withdrawal.confirmFailed')} />
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
          {t('withdrawal.confirm')}
        </Button>
      </div>
    </form>
  );
}

/**
 * The customer's withdrawal declaration for distance contracts, `/portal/withdrawal` (moved from
 * `/channels/withdrawal`). Reachable WITHOUT sign-in (open decision #2): the declaration is identified by the policy
 * id and answered by a one-time confirmation code sent to the e-mail on file.
 * TODO(hardening): the rate limit and the one-time token expiry are enforced by the API (chn.Withdrawal.declare /
 * .confirm); verify the limits with the CHN owner and add abuse alerting.
 */
export function WithdrawalPage() {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  usePortalTitle(t('withdrawal.title'));
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
    name: name.trim() === '' ? t('withdrawal.required') : undefined,
    contractId: uuidPattern.test(contractId.trim()) ? undefined : t('withdrawal.badContract'),
    eMail: /^\S+@\S+\.\S+$/.test(eMail.trim()) ? undefined : t('withdrawal.badEmail'),
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
    <PortalPage>
      <PortalHero
        overline={t('withdrawal.overline')}
        title={t('withdrawal.title')}
        lead={t('withdrawal.subtitle')}
      />
      {declared?.status === 'DECLINED' ? (
        <Banner variant="danger" live="alert" title={t('withdrawal.expiredTitle')}>
          {t('withdrawal.expiredBody', { number: declared.requestNumber })}
        </Banner>
      ) : declared ? (
        <PortalSection title={t('withdrawal.receipt')} family="success">
          <KeyValueList
            aria-label={t('withdrawal.receipt')}
            items={[
              {
                id: 'number',
                label: t('withdrawal.requestNumber'),
                value: declared.receipt.requestNumber,
                kind: 'mono',
              },
              {
                id: 'declared',
                label: t('withdrawal.declaredAt'),
                value: fmt.dateTime(declared.receipt.declaredAt),
              },
              {
                id: 'by',
                label: t('withdrawal.confirmBy'),
                value: declared.receipt.confirmBy ? fmt.dateTime(declared.receipt.confirmBy) : null,
              },
            ]}
          />
          <ConfirmStep declared={declared} />
        </PortalSection>
      ) : (
        <PortalSection title={t('withdrawal.formTitle')} family="teal">
          <form
            noValidate
            className={styles.stack}
            aria-label={t('withdrawal.formTitle')}
            onSubmit={(event) => {
              event.preventDefault();
              submit();
            }}
          >
            <TextField
              label={t('withdrawal.name')}
              value={name}
              onChange={setName}
              maxLength={200}
              errorMessage={tried ? errors.name : undefined}
            />
            <TextField
              label={t('withdrawal.contractId')}
              helperText={t('withdrawal.contractIdHelp')}
              value={contractId}
              onChange={setContractId}
              errorMessage={tried ? errors.contractId : undefined}
            />
            <TextField
              label={t('withdrawal.eMail')}
              value={eMail}
              onChange={setEMail}
              maxLength={320}
              errorMessage={tried ? errors.eMail : undefined}
            />
            <TextField
              label={t('withdrawal.reason')}
              multiline
              maxLength={1000}
              value={reason}
              onChange={setReason}
            />
            {mutation.isError ? (
              <PortalProblem error={mutation.error} title={t('withdrawal.failed')} />
            ) : null}
            <div className={styles.actions}>
              <Button variant="primary" isLoading={mutation.isPending} onPress={submit}>
                {t('withdrawal.submit')}
              </Button>
            </div>
          </form>
        </PortalSection>
      )}
    </PortalPage>
  );
}
