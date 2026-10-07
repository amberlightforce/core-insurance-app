import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button, TextField } from '../../design-system';
import { PageHeader, Section } from '../staff/PageHeader';
import styles from '../staff/staff.module.css';

/** Finance landing: journal entries are read by policy number. */
export function FinanceHomePage() {
  const { t } = useTranslation('finance');
  const navigate = useNavigate();
  const [number, setNumber] = useState('');
  const [tried, setTried] = useState(false);
  const valid = /^[A-Za-z0-9][A-Za-z0-9-]{2,63}$/.test(number.trim());
  return (
    <div className={styles.page}>
      <PageHeader title={t('home.title')} overline={t('overline')} />
      <Section title={t('home.byPolicy')}>
        <form
          noValidate
          className={styles.row}
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (valid)
              void navigate(`/finance/journals/policy/${encodeURIComponent(number.trim())}`);
          }}
        >
          <TextField
            label={t('home.policyNumber')}
            helperText={t('home.help')}
            mono
            value={number}
            onChange={setNumber}
            errorMessage={tried && !valid ? t('home.invalid') : undefined}
          />
          <Button type="submit" variant="primary">
            {t('home.open')}
          </Button>
        </form>
      </Section>
    </div>
  );
}
