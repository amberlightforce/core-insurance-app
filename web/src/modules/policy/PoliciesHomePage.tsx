import { FilePlus2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { Button, EmptyState, TextField } from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { readRecent } from '../staff/recent';
import styles from '../staff/staff.module.css';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Policies landing. The slice has no policy search (only get by id), so staff start a quote, reopen a recently
 * viewed or bound policy, or enter a policy id.
 */
export function PoliciesHomePage() {
  const { t } = useTranslation('policy');
  const navigate = useNavigate();
  const [id, setId] = useState('');
  const [tried, setTried] = useState(false);
  const recent = readRecent('policy');
  const valid = uuid.test(id.trim());
  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        title={t('home.title')}
        overline={t('overline')}
        description={t('home.lead')}
        actions={
          <Button
            variant="primary"
            icon={FilePlus2}
            onPress={() => {
              void navigate('/policies/quotes/new');
            }}
          >
            {t('home.newQuote')}
          </Button>
        }
      />
      <div className={styles.grid}>
        <Section title={t('home.recent')} family="brand" count={recent.length}>
          {recent.length === 0 ? (
            <EmptyState
              kind="first-use"
              illustration={<></>}
              headingLevel={3}
              headline={t('home.emptyTitle')}
              description={t('home.emptyBody')}
            />
          ) : (
            <ul className={styles.linkList}>
              {recent.map((r) => (
                <li key={r.id}>
                  <LinkButton to={`/policies/${r.id}`}>
                    {t('home.recentPolicy', { number: r.label })}
                  </LinkButton>
                </li>
              ))}
            </ul>
          )}
        </Section>
        <Section title={t('home.openById')}>
          <form
            noValidate
            className={styles.row}
            onSubmit={(event) => {
              event.preventDefault();
              setTried(true);
              if (valid) void navigate(`/policies/${id.trim()}`);
            }}
          >
            <TextField
              label={t('home.id')}
              mono
              value={id}
              onChange={setId}
              errorMessage={tried && !valid ? t('home.idInvalid') : undefined}
            />
            <Button type="submit" variant="secondary">
              {t('home.open')}
            </Button>
          </form>
        </Section>
      </div>
    </div>
  );
}
