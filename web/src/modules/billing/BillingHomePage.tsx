import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button, EmptyState, Radio, RadioGroup, TextField } from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
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

  return (
    <div className={styles.page}>
      <PageHeader title={t('home.title')} overline={t('overline')} />
      <Section title={t('home.recent')}>
        {invoices.length + accounts.length === 0 ? (
          <EmptyState
            kind="first-use"
            headingLevel={3}
            headline={t('home.emptyTitle')}
            description={t('home.emptyBody')}
          />
        ) : (
          <div className={styles.stack}>
            {invoices.map((r) => (
              <LinkButton key={r.id} to={`/billing/invoices/${r.id}`}>
                {t('home.recentInvoice', { number: r.label })}
              </LinkButton>
            ))}
            {accounts.map((r) => (
              <LinkButton key={r.id} to={`/billing/accounts/${r.id}`}>
                {t('home.recentAccount', { number: r.label })}
              </LinkButton>
            ))}
          </div>
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
  );
}
