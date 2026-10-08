import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { isApiError } from '../../../api/client';
import { useIdempotencyKey } from '../../../api/idempotency';
import type { PolicyGetResponse } from '../../../api/types';
import {
  Banner,
  Button,
  Checkbox,
  Dialog,
  EmptyState,
  KeyValueList,
  StatusPill,
  TextField,
  announce,
} from '../../../design-system';
import { UnderwritingOutcome } from '../../quote/QuoteResult';
import { athensToday } from '../../quote/time';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import { rememberRecent } from '../../staff/recent';
import styles from '../../staff/staff.module.css';
import { useFormat } from '../../staff/useFormat';
import { TermStatusPill } from '../TermStatusPill';
import {
  acceptRenewal,
  createRenewal,
  getServicingJob,
  offerRenewal,
  quoteServicingJob,
  type JobQuote,
  type RenewalAcceptResponse,
  type RenewalOfferResponse,
} from './api';
import { athensDateOf, changeInstant, daysBetween, renewalLeadDays } from './logic';
import { ServicingPreviewView } from './ServicingPreviewView';
import { ServicingProblem } from './ServicingProblem';
import { useServicingPolicy } from './useServicingPolicy';

type Busy = null | 'create' | 'offer' | 'accept';

/** Renewal panel (SCR-POL-17 subset): Renew now, rated renewal with its referral state, offer, explicit acceptance. */
export function RenewalPage() {
  const { policyId = '' } = useParams();
  const policy = useServicingPolicy(policyId);
  return (
    <div className={styles.page}>
      <QueryView query={policy}>{(data) => <RenewalPanel key={policyId} data={data} />}</QueryView>
    </div>
  );
}

function RenewalPanel({ data }: { data: PolicyGetResponse }) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const { policy, term } = data;
  const status = policy.status ?? term?.state;
  const renewable = term?.state === 'IN_FORCE';

  const [job, setJob] = useState<{ jobId: string; versionNo: number } | null>(null);
  const [quote, setQuote] = useState<JobQuote | null>(null);
  const [offer, setOffer] = useState<RenewalOfferResponse | null>(null);
  const [accepted, setAccepted] = useState<RenewalAcceptResponse | null>(null);
  const [busy, setBusy] = useState<Busy>(null);
  const [error, setError] = useState<{ error: unknown; title: string } | null>(null);
  const [acceptOpen, setAcceptOpen] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [evidence, setEvidence] = useState('');

  const createKey = useIdempotencyKey();
  const quoteKey = useIdempotencyKey();
  const offerKey = useIdempotencyKey();
  const acceptKey = useIdempotencyKey();
  /** The acceptance time is fixed per dialog so that a retry sends an identical payload (same key). */
  const acceptedAt = useRef<string | null>(null);

  const today = athensToday();
  const expiry = term?.period.to ? athensDateOf(term.period.to) : undefined;
  const daysLeft = expiry ? daysBetween(today, expiry) : undefined;
  const inWindow = daysLeft !== undefined && daysLeft >= 0 && daysLeft <= renewalLeadDays;
  const preview = quote?.servicingPreview;

  const renewNow = async () => {
    if (!term) return;
    setError(null);
    setBusy('create');
    try {
      const request = { termId: term.termId };
      const created = job
        ? { jobId: job.jobId }
        : await createRenewal(request, createKey.keyFor(request));
      createKey.release();
      const { job: server } = await getServicingJob(created.jobId);
      const quoteRequest = { jobId: created.jobId, versionNo: server.currentVersionNo };
      const quoted = await quoteServicingJob(quoteRequest, quoteKey.keyFor(quoteRequest));
      quoteKey.release();
      setJob({ jobId: created.jobId, versionNo: quoted.versionNo });
      setQuote(quoted);
      announce(t('servicing.renew.rated.ready'));
    } catch (cause) {
      setError({ error: cause, title: t('servicing.renew.create.failed') });
    } finally {
      setBusy(null);
    }
  };

  const issueOffer = async () => {
    if (!job || !term) return;
    setError(null);
    setBusy('offer');
    try {
      const request = { jobId: job.jobId, termId: term.termId };
      const response = await offerRenewal(request, offerKey.keyFor(request));
      offerKey.release();
      setOffer(response);
      announce(t('servicing.renew.offer.issued'));
    } catch (cause) {
      setError({ error: cause, title: t('servicing.renew.offer.failed') });
    } finally {
      setBusy(null);
    }
  };

  const accept = async () => {
    if (!job || !term) return;
    setError(null);
    setBusy('accept');
    try {
      acceptedAt.current ??= changeInstant(today);
      const request = {
        jobId: job.jobId,
        termId: term.termId,
        channel: 'STAFF',
        acceptedAt: acceptedAt.current,
        ...(evidence.trim() ? { acceptanceEvidence: evidence.trim() } : {}),
      };
      const response = await acceptRenewal(request, acceptKey.keyFor(request));
      acceptKey.release();
      setAcceptOpen(false);
      setConfirmed(false);
      setAccepted(response);
      rememberRecent('policy', policy.policyId);
      announce(t('servicing.renew.accepted.title'));
    } catch (cause) {
      setAcceptOpen(false);
      setConfirmed(false);
      acceptedAt.current = null;
      setError({ error: cause, title: t('servicing.renew.accept.failed') });
    } finally {
      setBusy(null);
    }
  };

  const header = (
    <PageHeader
      overline={[t('servicing.renew.overline'), policy.productCode].join(' · ')}
      title={t('servicing.renew.title')}
      recordId={policy.policyNumber}
      subtitle={status ? <TermStatusPill state={status} /> : undefined}
      facts={
        term
          ? [
              { id: 'from', label: t('term.from'), value: fmt.date(term.period.from) },
              {
                id: 'to',
                label: t('term.to'),
                value: term.period.to ? fmt.date(term.period.to) : t('term.open'),
              },
              { id: 'number', label: t('term.number'), value: String(term.termNumber) },
            ]
          : []
      }
      actions={
        <LinkButton variant="secondary" to={`/policies/${policy.policyId}`}>
          {t('servicing.back')}
        </LinkButton>
      }
    />
  );

  if (!renewable) {
    return (
      <>
        {header}
        <EmptyState
          kind="first-use"
          headingLevel={2}
          headline={t('servicing.renew.notRenewable.title')}
          description={t('servicing.renew.notRenewable.body')}
          action={
            <LinkButton variant="primary" to={`/policies/${policy.policyId}`}>
              {t('servicing.back')}
            </LinkButton>
          }
        />
      </>
    );
  }

  const rebase = error && isApiError(error.error) && error.error.code === 'POL-ERR-REBASE-REQUIRED';
  const referred = quote !== null && (quote.decision !== 'ACCEPT' || !quote.bindable);
  const stage = accepted ? 'accepted' : offer ? 'offered' : quote ? 'rated' : 'none';

  return (
    <>
      {header}
      {error ? (
        <ServicingProblem
          error={error.error}
          title={error.title}
          onRetry={() => void (job ? issueOffer() : renewNow())}
        />
      ) : null}
      {rebase ? (
        <div className={styles.actions}>
          <Button
            variant="secondary"
            isLoading={busy === 'offer'}
            onPress={() => {
              setOffer(null);
              void issueOffer();
            }}
          >
            {t('servicing.renew.accept.reoffer')}
          </Button>
        </div>
      ) : null}
      <Section title={t('servicing.renew.window.title')} family={inWindow ? 'success' : 'brand'}>
        <KeyValueList
          aria-label={t('servicing.renew.window.title')}
          items={[
            {
              id: 'stage',
              label: t('servicing.renew.stage.label'),
              value: <StatusPill semantic={stage === 'accepted' ? 'success' : 'info'} subLabel={t(`servicing.renew.stage.${stage}`)} announceChanges={false} />,
            },
            { id: 'expiry', label: t('servicing.renew.window.expiry'), value: expiry ? fmt.date(expiry) : null },
            {
              id: 'left',
              label: t('servicing.renew.window.daysLeft'),
              value: daysLeft !== undefined ? String(daysLeft) : null,
            },
            {
              id: 'lead',
              label: t('servicing.renew.window.lead'),
              value: t('servicing.renew.window.leadDays', { count: renewalLeadDays }),
            },
          ]}
        />
        {!inWindow && stage === 'none' ? (
          <Banner variant="warning" live="status" title={t('servicing.renew.window.outsideTitle')}>
            {t('servicing.renew.window.outsideBody', { count: renewalLeadDays })}
          </Banner>
        ) : null}
        {stage === 'none' ? (
          <div className={styles.actions}>
            <Button variant="primary" isLoading={busy === 'create'} onPress={() => void renewNow()}>
              {t('servicing.renew.create.commit')}
            </Button>
          </div>
        ) : null}
      </Section>
      {accepted ? (
        <>
          <Banner variant="success" live="status" title={t('servicing.renew.accepted.title')}>
            {t('servicing.renew.accepted.body', { number: accepted.newTermNumber })}
          </Banner>
          <KeyValueList
            aria-label={t('servicing.renew.accepted.title')}
            items={[
              { id: 'term', label: t('servicing.renew.accepted.newTerm'), value: String(accepted.newTermNumber) },
              {
                id: 'state',
                label: t('servicing.renew.accepted.state'),
                value: accepted.termState ? <TermStatusPill state={accepted.termState} /> : null,
              },
            ]}
          />
          <div className={styles.actions}>
            <LinkButton variant="primary" to={`/policies/${policy.policyId}`}>
              {t('servicing.openPolicy')}
            </LinkButton>
          </div>
        </>
      ) : null}
      {quote && !accepted ? (
        <>
          <Section title={t('servicing.renew.rated.title')}>
            <p className={styles.muted}>
              {t('servicing.renew.rated.body')}
            </p>
            <UnderwritingOutcome quote={quote} />
            {referred ? (
              <Banner variant="warning" live="status" title={t('servicing.renew.rated.referredTitle')}>
                {t('servicing.renew.rated.referredBody')}{' '}
                <LinkButton to="/policies/referrals">{t('servicing.renew.rated.openReferrals')}</LinkButton>
              </Banner>
            ) : null}
            {preview ? (
              <ServicingPreviewView preview={preview} />
            ) : (
              <Banner variant="warning" live="status" title={t('servicing.change.preview.noPreview')}>
                {t('servicing.change.preview.noPreviewBody')}
              </Banner>
            )}
          </Section>
          <Section title={t('servicing.renew.offer.title')}>
            {offer ? (
              <>
                <Banner variant="success" live="status" title={t('servicing.renew.offer.issued')}>
                  {t('servicing.renew.offer.body', { version: offer.offerVersion })}
                </Banner>
                <KeyValueList
                  aria-label={t('servicing.renew.offer.title')}
                  items={[
                    { id: 'version', label: t('servicing.renew.offer.version'), value: String(offer.offerVersion) },
                    { id: 'premium', label: t('servicing.renew.offer.premium'), value: fmt.money(offer.premiumSummary.total), kind: 'money' },
                    { id: 'deadline', label: t('servicing.renew.offer.deadline'), value: fmt.dateTime(offer.deadline) },
                    { id: 'mode', label: t('servicing.renew.offer.mode'), value: offer.acceptanceMode, kind: 'mono' },
                  ]}
                />
                <div className={styles.actions}>
                  <Button
                    variant="commit"
                    onPress={() => {
                      acceptedAt.current = null;
                      setAcceptOpen(true);
                    }}
                  >
                    {t('servicing.renew.accept.commit')}
                  </Button>
                </div>
              </>
            ) : (
              <>
                <p className={styles.muted}>{t('servicing.renew.offer.help')}</p>
                <div className={styles.actions}>
                  <Button
                    variant="primary"
                    isLoading={busy === 'offer'}
                    {...(referred ? { disabledReason: t('servicing.renew.offer.referredReason') } : {})}
                    onPress={() => void issueOffer()}
                  >
                    {t('servicing.renew.offer.commit')}
                  </Button>
                </div>
              </>
            )}
          </Section>
        </>
      ) : null}
      {!quote && !accepted ? (
        <EmptyState
          kind="first-use"
          headingLevel={2}
          headline={t('servicing.renew.emptyTitle')}
          description={t('servicing.renew.emptyBody')}
        />
      ) : null}
      <Dialog
        title={t('servicing.renew.accept.dialog.title')}
        tone="brand"
        isOpen={acceptOpen}
        isBusy={busy === 'accept'}
        onOpenChange={(open) => {
          setAcceptOpen(open);
          if (!open) setConfirmed(false);
        }}
        closeOnAction={false}
        primaryAction={{
          label: t('servicing.renew.accept.dialog.confirm'),
          variant: 'commit',
          ...(confirmed ? {} : { disabledReason: t('servicing.renew.accept.dialog.confirmRequired') }),
          onAction: () => {
            if (confirmed) void accept();
          },
        }}
      >
        <p>{t('servicing.renew.accept.dialog.body')}</p>
        <TextField
          label={t('servicing.renew.accept.dialog.evidence')}
          helperText={t('servicing.renew.accept.dialog.evidenceHelp')}
          value={evidence}
          onChange={setEvidence}
        />
        <Checkbox isSelected={confirmed} onChange={setConfirmed}>
          {t('servicing.renew.accept.dialog.check')}
        </Checkbox>
      </Dialog>
    </>
  );
}
