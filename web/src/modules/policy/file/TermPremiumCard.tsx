import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';

import type { components as Bil } from '../../../api/generated/bil';

import { SkeletonBlock } from '../../../design-system';
import { useInvoices } from '../../billing/api';
import { LinkButton } from '../../staff/LinkButton';
import { useFormat } from '../../staff/useFormat';
import type { TermViewModel, TimelineTransaction } from './api';
import { invoiceTotals, termPremium, toMoney } from './model';
import styles from './PolicyFile.module.css';

/**
 * The term premium card (the claim file's money card): net premium as the big number, written and credits under
 * it, then what billing shows for the term (billed / paid) with a link to the invoice. Sums are BigInt minor units.
 */
export function TermPremiumCard({
  policyId,
  term,
  transactions,
}: {
  policyId: string;
  term: TermViewModel;
  /** `undefined` while the timeline is loading or unavailable. */
  transactions: readonly TimelineTransaction[] | undefined;
}) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const invoices = useInvoices({ policyId });
  const premium = useMemo(
    () => (transactions ? termPremium(transactions, term.currency) : null),
    [transactions, term.currency],
  );
  const termInvoices = useMemo(
    () =>
      ((invoices.data?.items ?? []) as Bil['schemas']['InvoiceListItem'][]).filter(
        (x) => x.invoice.policyTermId === term.termId,
      ),
    [invoices.data, term.termId],
  );
  const totals = useMemo(
    () =>
      invoiceTotals(
        termInvoices.map((x) => x.invoice),
        term.currency,
      ),
    [termInvoices, term.currency],
  );
  const firstInvoice = termInvoices[0]?.invoice;

  return (
    <div className={styles.money}>
      {premium ? (
        <>
          <div className={styles.net}>
            <span className={styles.netLabel}>{t('file.premium.net')}</span>
            <span className={styles.netValue}>{fmt.money(toMoney(premium.net))}</span>
            <span className={styles.muted}>
              {t('file.premium.split', {
                written: fmt.money(toMoney(premium.written)),
                credits: fmt.money(toMoney(premium.credits)),
              })}
            </span>
          </div>
        </>
      ) : transactions === undefined ? (
        <p className={styles.muted}>{t('file.premium.unavailable')}</p>
      ) : null}
      {invoices.isPending ? (
        <div aria-busy="true">
          <SkeletonBlock width="60%" />
        </div>
      ) : invoices.isError ? (
        <p className={styles.muted}>{t('file.premium.billingUnavailable')}</p>
      ) : termInvoices.length > 0 ? (
        <ul className={styles.lines} aria-label={t('file.premium.billing')}>
          <li>
            <span>{t('file.premium.billed')}</span>
            <span className={styles.amount}>{fmt.money(toMoney(totals.billed))}</span>
          </li>
          <li>
            <span>{t('file.premium.paid')}</span>
            <span className={styles.amount}>{fmt.money(toMoney(totals.paid))}</span>
          </li>
        </ul>
      ) : (
        <p className={styles.muted}>{t('file.premium.noInvoices')}</p>
      )}
      {firstInvoice ? (
        <div className={styles.moneyActions}>
          <LinkButton variant="secondary" to={`/billing/invoices/${firstInvoice.invoiceId}`}>
            {t('file.premium.openInvoice', { number: firstInvoice.invoiceNumber })}
          </LinkButton>
        </div>
      ) : null}
    </div>
  );
}
