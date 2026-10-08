import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { isApiError } from '../../api/client';
import { useIdempotencyKey } from '../../api/idempotency';
import type { ClaimView, ExposureView } from '../../api/types';
import {
  Banner,
  Button,
  EmptyState,
  KeyValueList,
  Select,
  Tabs,
  announce,
  useTabSearchParam,
  type TabItem,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { QueryView } from '../staff/QueryView';
import { rememberRecent } from '../staff/recent';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { useClaim, useCloseClaim, useCreateExposure, usePolicyCoverages } from './api';
import { ClaimStatusPill } from './ClaimStatusPill';
import { closeGuardFindings } from './closeGuard';
import { closeOutcomes, exposureDuplicateReasons } from './codes';
import { FinancialsTab } from './FinancialsTab';
import { TruncatedRef } from './TruncatedRef';

function ExposureForm({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const { keyFor, release } = useIdempotencyKey();
  // The coverages come from the policy; the own-damage one is preselected.
  const covers = usePolicyCoverages(claim.summary.policyId);
  const [picked, setCoverage] = useState<string | null>(null);
  const coverage = picked ?? covers.data?.ownDamage ?? null;
  const [duplicateReason, setDuplicateReason] = useState<string | null>(null);
  const mutation = useCreateExposure(claim.summary.claimId);
  const duplicate =
    isApiError(mutation.error) && mutation.error.code === 'CLM-ERR-EXPOSURE-DUPLICATE';

  const submit = () => {
    if (!coverage) return;
    const request = {
      claimId: claim.summary.claimId,
      expectedRecordVersion: claim.summary.recordVersion,
      kind: 'OWN_DAMAGE' as const,
      coverageCode: coverage,
      ...(duplicateReason ? { duplicateReason } : {}),
    };
    mutation.mutate(
      { request, key: keyFor(request) },
      {
        onSuccess: (response) => {
          release();
          setDuplicateReason(null);
          announce(t('exposure.created', { number: response.exposure.exposureNumber }));
        },
      },
    );
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('exposure.new')}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <p className={styles.muted}>{t('exposure.intro')}</p>
      <div className={styles.grid}>
        <Select
          label={t('exposure.kind')}
          isRequired
          options={[{ id: 'OWN_DAMAGE', label: t('exposure.kinds.OWN_DAMAGE') }]}
          value="OWN_DAMAGE"
          onChange={() => undefined}
        />
        <Select
          label={t('exposure.coverage')}
          isRequired
          helperText={t('exposure.coverageIllustrative')}
          options={(covers.data?.codes ?? []).map((c) => ({ id: c, label: c }))}
          value={coverage}
          onChange={setCoverage}
        />
        {duplicate ? (
          <Select
            label={t('exposure.duplicateReason')}
            isRequired
            helperText={t('exposure.duplicateReasonHelp')}
            options={exposureDuplicateReasons.map((r) => ({
              id: r,
              label: t(`codes.exposureDuplicateReason.${r}`),
            }))}
            value={duplicateReason}
            onChange={setDuplicateReason}
          />
        ) : null}
      </div>
      {mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('exposure.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button
          type="submit"
          variant="primary"
          isLoading={mutation.isPending}
          isDisabled={duplicate && !duplicateReason}
        >
          {t('exposure.create')}
        </Button>
      </div>
    </form>
  );
}

function CloseForm({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const { keyFor, release } = useIdempotencyKey();
  const [outcome, setOutcome] = useState<string | null>(null);
  const [tried, setTried] = useState(false);
  const mutation = useCloseClaim(claim.summary.claimId);
  const findings = closeGuardFindings(mutation.error);

  const submit = () => {
    setTried(true);
    if (!outcome) return;
    const request = {
      claimId: claim.summary.claimId,
      expectedRecordVersion: claim.summary.recordVersion,
      outcome: outcome as (typeof closeOutcomes)[number],
    };
    mutation.mutate(
      { request, key: keyFor(request) },
      {
        onSuccess: () => {
          release();
          announce(t('close.closed'));
        },
      },
    );
  };

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={t('close.title')}
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <p className={styles.muted}>{t('close.intro')}</p>
      <Select
        label={t('close.outcome')}
        isRequired
        options={closeOutcomes.map((o) => ({ id: o, label: t(`codes.outcome.${o}`) }))}
        value={outcome}
        onChange={(value) => {
          setOutcome(value);
        }}
        errorMessage={tried && !outcome ? t('close.errors.outcome') : undefined}
      />
      {findings ? (
        <Banner variant="danger" live="alert" title={t('close.guard.title')}>
          <p>{t('close.guard.body')}</p>
          <ul className={styles.problemList}>
            {findings.map((f) => (
              <li key={f.exposure}>
                <span className="ds-mono">{f.exposure}</span>:{' '}
                {f.reasons.map((r) => t(`close.guard.reason.${r}`)).join('; ')}
              </li>
            ))}
          </ul>
        </Banner>
      ) : mutation.isError ? (
        <ProblemBanner error={mutation.error} title={t('close.failed')} />
      ) : null}
      <div className={styles.actions}>
        <Button type="submit" variant="secondary" isLoading={mutation.isPending}>
          {t('close.submit')}
        </Button>
      </div>
    </form>
  );
}

function ClaimDetails({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const fmt = useFormat();
  const summary = claim.summary;
  const open = summary.status === 'OPEN';

  useEffect(() => {
    rememberRecent('claim', { id: summary.claimId, label: summary.claimNumber });
  }, [summary.claimId, summary.claimNumber]);

  const exposureColumns = useMemo<DataColumn<ExposureView>[]>(
    () => [
      identifierColumn<ExposureView>(
        'number',
        t('exposure.columns.number'),
        (e) => e.exposureNumber,
        {
          size: 180,
        },
      ),
      textColumn<ExposureView>('kind', t('exposure.columns.kind'), (e) =>
        t(`exposure.kinds.${e.kind}`),
      ),
      identifierColumn<ExposureView>(
        'coverage',
        t('exposure.columns.coverage'),
        (e) => e.coverageCode,
        {
          size: 120,
        },
      ),
      statusColumn<ExposureView>(
        'status',
        t('exposure.columns.status'),
        (e) => e.status,
        (e) => <ClaimStatusPill entity="exposure" status={e.status} />,
      ),
      textColumn<ExposureView>('indication', t('exposure.columns.indication'), (e) =>
        t(`indication.${e.coverageIndication}`),
      ),
      textColumn<ExposureView>('decision', t('exposure.columns.decision'), (e) =>
        t(`decision.${e.coverageDecision}`),
      ),
    ],
    [t],
  );

  const [tab, setTab] = useTabSearchParam('tab', 'overview');
  const tabItems = useMemo<TabItem[]>(
    () => [
      { id: 'overview', label: t('view.tabs.overview') },
      { id: 'financials', label: t('view.tabs.financials') },
    ],
    [t],
  );

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('overline')}
        title={t('view.title', { number: summary.claimNumber })}
        subtitle={
          <>
            <ClaimStatusPill status={summary.status} subStatus={summary.subStatus} />
            {summary.outcome ? (
              <span className="ds-caption">{t(`codes.outcome.${summary.outcome}`)}</span>
            ) : null}
          </>
        }
        actions={
          <>
            <LinkButton variant="secondary" to={`/policies/${summary.policyId}`}>
              {t('view.openPolicy')}
            </LinkButton>
            <LinkButton variant="secondary" to={`/parties/${summary.insuredPartyId}`}>
              {t('view.openInsured')}
            </LinkButton>
          </>
        }
      />
      {summary.coverageInQuestion ? (
        <Banner variant="warning" live="none" title={t('fnol.coverageInQuestion.title')}>
          {t('fnol.coverageInQuestion.body')}
        </Banner>
      ) : null}
      {summary.snapshotStatus !== 'VERIFIED' ? (
        <Banner variant="info" live="none" title={t('view.snapshot.notVerifiedTitle')}>
          {t(`view.snapshot.status.${summary.snapshotStatus}`)}
        </Banner>
      ) : null}
      <Tabs
        aria-label={t('view.tabs.label')}
        items={tabItems}
        {...(tab ? { selectedKey: tab } : {})}
        onSelectionChange={setTab}
      >
        {(item) =>
          item.id === 'financials' ? (
            <FinancialsTab claim={claim} />
          ) : (
            <div className={styles.stack}>
              <div className={styles.grid}>
                <Section title={t('view.header.title')}>
                  <KeyValueList
                    aria-label={t('view.header.title')}
                    items={[
                      {
                        id: 'policy',
                        label: t('view.header.policy'),
                        value: summary.policyNumber,
                        kind: 'mono',
                      },
                      {
                        id: 'product',
                        label: t('view.header.product'),
                        value: summary.productVersion
                          ? `${summary.productCode} ${summary.productVersion}`
                          : summary.productCode,
                        kind: 'mono',
                      },
                      {
                        id: 'loss',
                        label: t('view.header.lossDate'),
                        value: fmt.dateTime(summary.lossAt),
                      },
                      {
                        id: 'notice',
                        label: t('view.header.noticeOn'),
                        value: fmt.date(summary.noticeOn),
                      },
                      {
                        id: 'cause',
                        label: t('view.header.cause'),
                        value: t(`codes.lossCause.${summary.lossCause}`),
                      },
                      {
                        id: 'segment',
                        label: t('view.header.segment'),
                        value: summary.handlingSegment,
                        kind: 'mono',
                      },
                      {
                        id: 'handler',
                        label: t('view.header.handler'),
                        value: summary.handler ?? null,
                      },
                      {
                        id: 'open',
                        label: t('view.header.openDays'),
                        value: String(summary.openDays),
                      },
                    ]}
                  />
                </Section>
                <Section title={t('view.coverage.title')}>
                  <KeyValueList
                    aria-label={t('view.coverage.title')}
                    items={[
                      {
                        id: 'inForce',
                        label: t('view.coverage.inForce'),
                        value: summary.policyInForceAtLoss
                          ? t('view.coverage.yes')
                          : t('view.coverage.no'),
                      },
                      {
                        id: 'statusAtLoss',
                        label: t('view.coverage.statusAtLoss'),
                        value: summary.policyStatusAtLoss ?? null,
                        kind: 'mono',
                      },
                      {
                        id: 'indication',
                        label: t('view.coverage.indication'),
                        value: summary.coverageInQuestion
                          ? t('indication.IN_QUESTION')
                          : t('view.coverage.perSnapshot'),
                      },
                      {
                        id: 'snapshot',
                        label: t('view.coverage.snapshotRef'),
                        value: <TruncatedRef value={summary.snapshotRef} />,
                      },
                      {
                        id: 'known',
                        label: t('view.coverage.snapshotKnownAt'),
                        value: fmt.dateTime(summary.snapshotKnownAt),
                      },
                    ]}
                  />
                </Section>
              </div>
              <Section title={t('view.loss.title')}>
                <KeyValueList
                  aria-label={t('view.loss.title')}
                  items={[
                    { id: 'location', label: t('view.loss.location'), value: claim.lossLocation },
                    {
                      id: 'description',
                      label: t('view.loss.description'),
                      value: claim.description,
                    },
                  ]}
                />
              </Section>
              <Section title={t('exposure.title')}>
                <SimpleTable<ExposureView>
                  aria-label={t('exposure.title')}
                  columns={exposureColumns}
                  data={claim.exposures}
                  getRowId={(e) => e.exposureId}
                  emptyState={
                    <EmptyState
                      kind="first-use"
                      headingLevel={3}
                      headline={t('exposure.emptyTitle')}
                      description={t('exposure.emptyBody')}
                    />
                  }
                />
              </Section>
              {open ? (
                <Section title={t('exposure.new')}>
                  <ExposureForm claim={claim} />
                </Section>
              ) : null}
              {open ? (
                <Section title={t('close.title')}>
                  <CloseForm claim={claim} />
                </Section>
              ) : (
                <Banner variant="info" live="none" title={t('close.alreadyClosed')}>
                  {summary.closedAt
                    ? t('close.closedAt', { date: fmt.dateTime(summary.closedAt) })
                    : null}
                </Banner>
              )}
            </div>
          )
        }
      </Tabs>
    </div>
  );
}

/** SCR-CLM-02/03 subset: the claim header, coverage indication, exposures, new exposure, payee account, close. */
export function ClaimViewPage() {
  const { t } = useTranslation('claims');
  const { claimId = '' } = useParams();
  const query = useClaim(claimId);
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('view.notFound')}>
        {(data) => <ClaimDetails claim={data.claim} />}
      </QueryView>
    </div>
  );
}
