import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-aria-components';
import { useNavigate, useParams } from 'react-router';

import { isApiError } from '../../api/client';
import { useIdempotencyKey } from '../../api/idempotency';
import type { ClaimView, ExposureView, PartyView } from '../../api/types';
import {
  Banner,
  Button,
  EmptyState,
  KeyValueList,
  Select,
  StatusPill,
  Tabs,
  announce,
  useTabSearchParam,
  type TabItem,
  identifierColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { cx } from '../../design-system/utils/cx';
import { useParty } from '../party/api';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section } from '../staff/PageHeader';
import { ProblemBanner } from '../staff/ProblemBanner';
import { QueryView } from '../staff/QueryView';
import { rememberRecent } from '../staff/recent';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import {
  useClaim,
  useCloseClaim,
  useCreateExposure,
  useFinancials,
  usePayments,
  usePolicyCoverages,
} from './api';
import file from './ClaimFile.module.css';
import { ClaimHistory, ClaimMoney } from './ClaimMoney';
import { ClaimStatusPill } from './ClaimStatusPill';
import { closeGuardFindings } from './closeGuard';
import { closeOutcomes, exposureDuplicateReasons } from './codes';
import { FinancialsTab } from './FinancialsTab';
import { ReverifyBanner } from './reverify/ReverifyBanner';
import { StageStrip } from './StageStrip';
import { amountOf, claimStages } from './stages';
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

/** The party's native-script name (the claim carries only the insured's id). */
function partyName(party: PartyView): string {
  const name = party.names.find((n) => n.form === 'NATIVE') ?? party.names[0];
  if (!name) return party.partyNumber;
  return (
    name.organisationName ??
    ([name.givenNames, name.familyName].filter(Boolean).join(' ') || party.partyNumber)
  );
}

function ClaimDetails({ claim }: { claim: ClaimView }) {
  const { t } = useTranslation('claims');
  const navigate = useNavigate();
  const fmt = useFormat();
  const summary = claim.summary;
  const open = summary.status === 'OPEN';

  useEffect(() => {
    rememberRecent('claim', summary.claimId);
  }, [summary.claimId]);

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

  // The insured's name is the file's title (mockup «Φάκελος ζημίας»); the claim number stays next to it in mono.
  const insured = useParty(summary.insuredPartyId);
  const insuredName = insured.data ? partyName(insured.data.party) : null;
  const financials = useFinancials(summary.claimId);
  const payments = usePayments(summary.claimId);
  const stages = claimStages(claim, {
    reserved: financials.data ? amountOf(financials.data.totals.reserved) : null,
    payments: payments.data ? payments.data.items : null,
  });
  // Codes outside the illustrative list (e.g. seeded data) show as the code, never as a translation key.
  const cause = t(`codes.lossCause.${summary.lossCause}`, { defaultValue: summary.lossCause });

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={[t('overline'), summary.productCode, cause].join(' · ')}
        title={insuredName ?? t('view.title', { number: summary.claimNumber })}
        {...(insuredName ? { recordId: summary.claimNumber } : {})}
        subtitle={
          <>
            <ClaimStatusPill status={summary.status} subStatus={summary.subStatus} />
            {summary.coverageInQuestion ? (
              <StatusPill
                semantic="warning"
                subLabel={t('indication.IN_QUESTION')}
                announceChanges={false}
              />
            ) : null}
            {summary.outcome ? (
              <span className="ds-caption">{t(`codes.outcome.${summary.outcome}`)}</span>
            ) : null}
          </>
        }
        facts={[
          { id: 'loss', label: t('file.facts.loss'), value: fmt.dateTime(summary.lossAt) },
          { id: 'cause', label: t('file.facts.cause'), value: cause },
          {
            id: 'policy',
            label: t('file.facts.policy'),
            value: (
              <Link href={`/policies/${summary.policyId}`} className={cx(file.factLink)}>
                {summary.policyNumber}
              </Link>
            ),
          },
          {
            id: 'open',
            label: t('file.facts.open'),
            value: t('file.facts.days', { count: summary.openDays }),
          },
          ...(summary.handler
            ? [{ id: 'handler', label: t('file.facts.handler'), value: summary.handler }]
            : []),
        ]}
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
      >
        <StageStrip stages={stages} />
      </PageHeader>
      {summary.coverageInQuestion ? (
        <Banner variant="warning" live="none" title={t('fnol.coverageInQuestion.title')}>
          {t('fnol.coverageInQuestion.body')}
        </Banner>
      ) : null}
      <ReverifyBanner claim={claim} />
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
            <div className={styles.split}>
              <div className={styles.stack}>
                <Section
                  title={t('exposure.title')}
                  meta={t('file.exposuresMeta')}
                  count={claim.exposures.length}
                >
                  <SimpleTable<ExposureView>
                    aria-label={t('exposure.title')}
                    columns={exposureColumns}
                    data={claim.exposures}
                    getRowId={(e) => e.exposureId}
                    emptyState={
                      <EmptyState
                        kind="first-use"
                        headingLevel={3}
                        illustration={<></>}
                        headline={t('exposure.emptyTitle')}
                        description={t('exposure.emptyBody')}
                      />
                    }
                  />
                </Section>
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
                          value: cause,
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
              <div className={styles.stack}>
                <Section title={t('file.money.title')} family="success">
                  <ClaimMoney
                    claim={claim}
                    onNewPayment={() => {
                      setTab('financials');
                    }}
                    onRelease={() => {
                      void navigate('/claims/approvals');
                    }}
                  />
                </Section>
                <Section title={t('file.history.title')}>
                  <ClaimHistory claim={claim} />
                </Section>
              </div>
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
