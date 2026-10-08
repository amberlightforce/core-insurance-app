import { useMutation } from '@tanstack/react-query';
import { FilePlus2, Inbox } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import type { ClaimSearchCriteria, ClaimSummary } from '../../api/types';
import {
  Button,
  EmptyState,
  TextField,
  dateColumn,
  identifierColumn,
  statusColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { readRecent } from '../staff/recent';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { searchClaims } from './api';
import { ClaimStatusPill } from './ClaimStatusPill';

/** Claims landing: search by claim number and/or policy number (POST body, never in the URL), recent claims. */
export function ClaimsHomePage() {
  const { t } = useTranslation('claims');
  const navigate = useNavigate();
  const [claimNumber, setClaimNumber] = useState('');
  const [policyNumber, setPolicyNumber] = useState('');
  const [tried, setTried] = useState(false);
  const recent = readRecent('claim');
  const empty = claimNumber.trim() === '' && policyNumber.trim() === '';

  const search = useMutation({
    mutationFn: (criteria: ClaimSearchCriteria) => searchClaims(criteria),
  });

  const rows = useMemo<ClaimSummary[]>(
    () => (search.data?.items as { claim: ClaimSummary }[] | undefined)?.map((i) => i.claim) ?? [],
    [search.data],
  );

  const columns = useMemo<DataColumn<ClaimSummary>[]>(
    () => [
      identifierColumn<ClaimSummary>('number', t('columns.claimNumber'), (c) => c.claimNumber, {
        size: 160,
      }),
      statusColumn<ClaimSummary>(
        'status',
        t('columns.status'),
        (c) => c.status,
        (c) => <ClaimStatusPill status={c.status} subStatus={c.subStatus} />,
      ),
      identifierColumn<ClaimSummary>('policy', t('columns.policy'), (c) => c.policyNumber, {
        size: 160,
      }),
      dateColumn<ClaimSummary>('loss', t('columns.lossDate'), (c) => c.lossDate),
      dateColumn<ClaimSummary>('notice', t('columns.noticeOn'), (c) => c.noticeOn),
    ],
    [t],
  );

  return (
    <div className={styles.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('home.title')}
        description={t('home.lead')}
        actions={
          <>
            <Button
              variant="primary"
              icon={FilePlus2}
              onPress={() => {
                void navigate('/claims/new');
              }}
            >
              {t('home.newFnol')}
            </Button>
            <Button
              variant="secondary"
              icon={Inbox}
              onPress={() => {
                void navigate('/claims/approvals');
              }}
            >
              {t('home.approvals')}
            </Button>
          </>
        }
      />
      <Section title={t('search.title')}>
        <form
          noValidate
          className={styles.stack}
          onSubmit={(event) => {
            event.preventDefault();
            setTried(true);
            if (empty) return;
            search.mutate({
              ...(claimNumber.trim() ? { claimNumber: claimNumber.trim() } : {}),
              ...(policyNumber.trim() ? { policyNumber: policyNumber.trim() } : {}),
            });
          }}
        >
          <div className={styles.grid}>
            <TextField
              label={t('search.claimNumber')}
              mono
              value={claimNumber}
              onChange={setClaimNumber}
              errorMessage={tried && empty ? t('search.needCriterion') : undefined}
            />
            <TextField
              label={t('search.policyNumber')}
              mono
              value={policyNumber}
              onChange={setPolicyNumber}
            />
          </div>
          <div className={styles.actions}>
            <Button type="submit" variant="primary" isLoading={search.isPending}>
              {t('search.submit')}
            </Button>
          </div>
        </form>
        {search.isError ? <ProblemBanner error={search.error} title={t('search.failed')} /> : null}
        {search.isSuccess ? (
          <SimpleTable<ClaimSummary>
            aria-label={t('search.results')}
            columns={columns}
            data={rows}
            getRowId={(c) => c.claimId}
            onOpen={(c) => {
              void navigate(`/claims/${c.claimId}`);
            }}
            emptyState={
              <EmptyState
                kind="first-use"
                headingLevel={3}
                headline={t('search.noResultsTitle')}
                description={t('search.noResultsBody')}
              />
            }
          />
        ) : null}
      </Section>
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
                <LinkButton to={`/claims/${r.id}`}>
                  {t('home.recentClaim', { number: r.label })}
                </LinkButton>
              </li>
            ))}
          </ul>
        )}
      </Section>
    </div>
  );
}
