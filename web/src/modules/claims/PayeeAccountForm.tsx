import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';

import { newIdempotencyKey } from '../../api/idempotency';
import type { ClaimPayeeAccountView } from '../../api/types';
import {
  Banner,
  Button,
  IdentifierField,
  KeyValueList,
  TextField,
  announce,
} from '../../design-system';
import { normalizeIban, validateIban } from '../../format';
import { ProblemBanner } from '../staff/ProblemBanner';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { capturePayeeAccount } from './api';

export interface PayeeAccountFormProps {
  claimId: string;
  /** PTY party of the payee: the claim's insured. */
  partyId: string;
  /** Called with the saved account (masked IBAN only) so the claim view can offer it. */
  onSaved?: (account: ClaimPayeeAccountView) => void;
}

/** A cheap, non-reversible fingerprint (FNV-1a) so the Idempotency-Key is reused only for the same input. */
function fingerprint(text: string): string {
  let hash = 0x811c9dc5;
  for (let i = 0; i < text.length; i += 1) {
    hash ^= text.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash.toString(16);
}

/**
 * Payee bank account capture (bil.PayeeAccount.create, purpose CLAIM_PAYMENT). The IBAN is personal data (P2): it
 * travels only in the request body, is never put in a URL, a query key, a log or the page's own state beyond the
 * input, and is cleared as soon as the account is saved; afterwards only the masked form (last four characters)
 * is shown. The mutation is called directly so react-query's mutation cache never holds the IBAN.
 */
export function PayeeAccountForm({ claimId, partyId, onSaved }: PayeeAccountFormProps) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const [holder, setHolder] = useState('');
  const [iban, setIban] = useState('');
  const [tried, setTried] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<unknown>(null);
  const [saved, setSaved] = useState<ClaimPayeeAccountView | null>(null);
  const key = useRef<{ print: string; value: string } | null>(null);

  const ibanIssue = iban === '' ? 'required' : validateIban(normalizeIban(iban)) ? 'invalid' : null;
  const holderIssue = holder.trim() === '' ? 'required' : null;
  const invalid = ibanIssue !== null || holderIssue !== null;

  const submit = async () => {
    setTried(true);
    setSaved(null);
    if (invalid) return;
    const normalized = normalizeIban(iban);
    const print = fingerprint(`${claimId}|${partyId}|${holder.trim()}|${normalized}`);
    if (key.current?.print !== print) key.current = { print, value: newIdempotencyKey() };
    setBusy(true);
    setFailure(null);
    try {
      const { payeeAccount: account } = await capturePayeeAccount(
        { claimId, partyId, iban: normalized, holderName: holder.trim() },
        key.current.value,
      );
      // Clear the secret as soon as it is saved; only the masked IBAN comes back.
      setIban('');
      setHolder('');
      setTried(false);
      key.current = null;
      setSaved(account);
      onSaved?.(account);
      announce(t('payee.saved'));
    } catch (error) {
      setFailure(error);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('payee.title')}
      autoComplete="off"
      onSubmit={(event) => {
        event.preventDefault();
        void submit();
      }}
    >
      <p className={styles.muted}>{t('payee.intro')}</p>
      <div className={styles.grid}>
        <TextField
          label={t('payee.holder')}
          isRequired
          autoComplete="off"
          maxLength={140}
          helperText={t('payee.holderHelp')}
          value={holder}
          onChange={setHolder}
          errorMessage={tried && holderIssue ? t('payee.errors.holder') : undefined}
        />
        <IdentifierField
          kind="iban"
          label={t('payee.iban')}
          isRequired
          value={iban}
          onChange={setIban}
          helperText={t('payee.ibanHelp')}
          errorMessage={
            tried && ibanIssue === 'required'
              ? t('payee.errors.ibanRequired')
              : tried && ibanIssue === 'invalid'
                ? t('payee.errors.ibanInvalid')
                : undefined
          }
        />
      </div>
      <KeyValueList
        aria-label={t('payee.fixed')}
        items={[
          { id: 'purpose', label: t('payee.purpose'), value: t('payee.purposeClaimPayment') },
        ]}
      />
      {failure ? <ProblemBanner error={failure} title={t('payee.failed')} /> : null}
      {saved ? (
        <Banner variant="success" live="status" title={t('payee.saved')}>
          <KeyValueList
            aria-label={t('payee.savedSummary')}
            items={[
              { id: 'iban', label: t('payee.maskedIban'), value: saved.maskedIban, kind: 'mono' },
              {
                id: 'verification',
                label: t('payee.verification'),
                value: t(`payee.verificationStatus.${saved.verificationStatus}`),
              },
              {
                id: 'cooling',
                label: t('payee.coolingOffUntil'),
                value: fmt.date(saved.coolingOffUntil),
              },
            ]}
          />
          {saved.change ? <p>{t('payee.changeNote')}</p> : null}
        </Banner>
      ) : null}
      <div className={styles.actions}>
        <Button type="submit" variant="primary" isLoading={busy}>
          {t('payee.submit')}
        </Button>
      </div>
    </form>
  );
}
