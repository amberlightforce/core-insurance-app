import { FilePlus2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { readSession } from '../../dev-auth/devAuth';
import { Button, EmptyState, TextField } from '../../design-system';
import { underwritingManagerRole } from '../quote/uwIssues';
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
  const { t } = useTranslation(['policy', 'uw']);
  const navigate = useNavigate();
  const [id, setId] = useState('');
  const [tried, setTried] = useState(false);
  const recent = readRecent('policy');
  // Senior underwriters decide referrals (the API enforces uw.Issue.decide; the link only follows the role).
  const canDecideReferrals = (readSession()?.user.roles ?? []).includes(underwritingManagerRole);
  const valid = uuid.test(id.trim());
  return (
    <div className={styles.page}>
      <PageHeader
        title={t('home.title')}
        overline={t('overline')}
        actions={
          <>
            {canDecideReferrals ? (
              <LinkButton variant="secondary" to="/policies/referrals">
                {t('uw:referrals.link')}
              </LinkButton>
            ) : null}
            <Button
              variant="primary"
              icon={FilePlus2}
              onPress={() => {
                void navigate('/policies/quotes/new');
              }}
            >
              {t('home.newQuote')}
            </Button>
          </>
        }
      />
      <Section title={t('home.recent')}>
        {recent.length === 0 ? (
          <EmptyState
            kind="first-use"
            headingLevel={3}
            headline={t('home.emptyTitle')}
            description={t('home.emptyBody')}
          />
        ) : (
          <div className={styles.stack}>
            {recent.map((r) => (
              <LinkButton key={r.id} to={`/policies/${r.id}`}>
                {t('home.recentPolicy', { number: r.label })}
              </LinkButton>
            ))}
          </div>
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
  );
}
