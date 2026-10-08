import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button, EmptyState, Radio, RadioGroup, TextField } from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { QueryView } from '../staff/QueryView';
import { useInvoices } from './api';
import { InvoiceTable } from './InvoiceTable';
import { PageHeader, Section } from '../staff/PageHeader';
import { readRecent } from '../staff/recent';
import styles from '../staff/staff.module.css';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Billing landing. The slice has no account or invoice search, so staff arrive from a policy (its invoices), from
 * a recently opened record, or by the record's id.
 */
export function BillingHomePage() {
  const { t } = useTranslation('billing');
  const navigate = useNavigate();
  const [kind, setKind] = useState('invoices');
  const [id, setId] = useState('');
  const [tried, setTried] = useState(false);
  const invoices = readRecent('invoice');
  const accounts = readRecent('account');
  const valid = uuid.test(id.trim());
  // bil.Invoice.list without a filter: the latest invoices of every account the user may see.
  const all = useInvoices({});

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('home.title')}
        overline={t('overline')}
        description={t('home.lead')}
      />
      <Section title={t('home.invoices')} family="teal" count={all.data?.items.length}>
        <QueryView query={all}>
          {(page) => <InvoiceTable items={page.items} label={t('home.invoices')} />}
        </QueryView>
      </Section>
      <div className={styles.grid}>
        <Section title={t('home.recent')} family="brand" count={invoices.length + accounts.length}>
          {invoices.length + accounts.length === 0 ? (
            <EmptyState
              kind="first-use"
              illustration={<></>}
              headingLevel={3}
              headline={t('home.emptyTitle')}
              description={t('home.emptyBody')}
            />
          ) : (
            <ul className={styles.linkList}>
              {invoices.map((r) => (
                <li key={r.id}>
                  <LinkButton to={`/billing/invoices/${r.id}`}>
                    {t('home.recentInvoice', { number: r.label })}
                  </LinkButton>
                </li>
              ))}
              {accounts.map((r) => (
                <li key={r.id}>
                  <LinkButton to={`/billing/accounts/${r.id}`}>
                    {t('home.recentAccount', { number: r.label })}
                  </LinkButton>
                </li>
              ))}
            </ul>
          )}
        </Section>
        <Section title={t('home.openById')}>
          <form
            noValidate
            className={styles.stack}
            onSubmit={(event) => {
              event.preventDefault();
              setTried(true);
              if (valid) void navigate(`/billing/${kind}/${id.trim()}`);
            }}
          >
            <RadioGroup
              label={t('home.kind')}
              value={kind}
              onChange={setKind}
              orientation="horizontal"
            >
              <Radio value="invoices">{t('home.kindInvoice')}</Radio>
              <Radio value="accounts">{t('home.kindAccount')}</Radio>
            </RadioGroup>
            <TextField
              label={t('home.id')}
              mono
              value={id}
              onChange={setId}
              errorMessage={tried && !valid ? t('home.idInvalid') : undefined}
            />
            <div className={styles.actions}>
              <Button type="submit" variant="primary">
                {t('home.open')}
              </Button>
            </div>
          </form>
        </Section>
      </div>
    </div>
  );
}
