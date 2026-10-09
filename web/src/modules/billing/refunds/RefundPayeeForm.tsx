import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { newIdempotencyKey } from '../../../api/idempotency';
import {
  Banner,
  Button,
  IdentifierField,
  KeyValueList,
  TextField,
  announce,
} from '../../../design-system';
import { normalizeIban, validateIban } from '../../../format';
import { ProblemBanner } from '../../staff/ProblemBanner';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { createRefundPayeeAccount, type PayeeAccountCreateResponse } from './api';

/** A cheap, non-reversible fingerprint (FNV-1a) so the Idempotency-Key is reused only for the same input. */
function fingerprint(text: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i += 1) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash.toString(16);
}

export interface RefundPayeeFormProps {
  /** PTY party of the payee: the payer of the billing account (D-SL3-14). */
  partyId: string;
  /** Called with the saved account (id and masked IBAN only). */
  onSaved: (account: PayeeAccountCreateResponse) => void;
}

/**
 * Payee bank account capture for refunds (bil.PayeeAccount.create, purpose REFUND). The IBAN is personal data (P2): it
 * travels only in the request body, never in a URL, a query key or browser storage, is cleared as soon as the account
 * is saved, and afterwards only the masked form is shown. The call is made directly (not through a mutation hook) so
 * react-query's mutation cache never holds the IBAN.
 */
export function RefundPayeeForm({ partyId, onSaved }: RefundPayeeFormProps) {
  const { t } = useTranslation('billing');
  const fmt = useFormat();
  const [holder, setHolder] = useState('');
  const [iban, setIban] = useState('');
  const [tried, setTried] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<unknown>(null);
  const [saved, setSaved] = useState<PayeeAccountCreateResponse | null>(null);
  const key = useRef<{ print: string; value: string } | null>(null);

  const ibanIssue = iban === '' ? 'required' : validateIban(normalizeIban(iban)) ? 'invalid' : null;
  const holderIssue = holder.trim() === '' ? 'required' : null;

  const submit = async () => {
    setTried(true);
    setSaved(null);
    if (ibanIssue !== null || holderIssue !== null) return;
    const normalized = normalizeIban(iban);
    const print = fingerprint(`${partyId}|${holder.trim()}|${normalized}`);
    if (key.current?.print !== print) key.current = { print, value: newIdempotencyKey() };
    setBusy(true);
    setFailure(null);
    try {
      const account = await createRefundPayeeAccount(
        { partyId, iban: normalized, holderName: holder.trim() },
        key.current.value,
      );
      // Clear the secret as soon as it is saved; only the masked IBAN comes back.
      setIban('');
      setHolder('');
      setTried(false);
      key.current = null;
      setSaved(account);
      onSaved(account);
      announce(t('refunds.payee.saved'));
    } catch (error) {
      setFailure(error);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.stack} role="group" aria-label={t('refunds.payee.submit')}>
      <p className={styles.muted}>{t('refunds.payee.intro')}</p>
      <div className={styles.grid}>
        <TextField
          label={t('refunds.payee.holder')}
          isRequired
          autoComplete="off"
          maxLength={140}
          helperText={t('refunds.payee.holderHelp')}
          value={holder}
          onChange={setHolder}
          errorMessage={tried && holderIssue ? t('refunds.payee.errors.holder') : undefined}
        />
        <IdentifierField
          kind="iban"
          label={t('refunds.payee.iban')}
          isRequired
          value={iban}
          onChange={setIban}
          helperText={t('refunds.payee.ibanHelp')}
          errorMessage={
            tried && ibanIssue === 'required'
              ? t('refunds.payee.errors.ibanRequired')
              : tried && ibanIssue === 'invalid'
                ? t('refunds.payee.errors.ibanInvalid')
                : undefined
          }
        />
      </div>
      {failure ? <ProblemBanner error={failure} title={t('refunds.payee.failed')} /> : null}
      {saved ? (
        <Banner variant="success" live="status" title={t('refunds.payee.saved')}>
          <KeyValueList
            aria-label={t('refunds.payee.savedSummary')}
            items={[
              {
                id: 'iban',
                label: t('refunds.payee.maskedIban'),
                value: saved.maskedIban,
                kind: 'mono',
              },
              {
                id: 'verification',
                label: t('refunds.payee.verification'),
                value: t(`refunds.verificationStatus.${saved.verificationStatus}`, {
                  defaultValue: saved.verificationStatus,
                }),
              },
              {
                id: 'cooling',
                label: t('refunds.payee.coolingOffUntil'),
                value: fmt.date(saved.coolingOffUntil),
              },
            ]}
          />
          {saved.change ? <p>{t('refunds.payee.changeNote')}</p> : null}
        </Banner>
      ) : null}
      <div className={styles.actions}>
        <Button
          variant="secondary"
          isLoading={busy}
          onPress={() => {
            void submit();
          }}
        >
          {t('refunds.payee.submit')}
        </Button>
      </div>
    </div>
  );
}
