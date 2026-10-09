import { parseDate } from '@internationalized/date';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import { Banner, DatePicker, KeyValueList, StatusPill } from '../../design-system';
import { InvoiceTable } from '../billing/InvoiceTable';
import { useInvoices } from '../billing/api';
import { athensToday } from '../quote/time';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section, type PageFact } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { rememberRecent } from '../staff/recent';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { ActionBar } from './file/ActionBar';
import {
  usePolicyAt,
  useSnapshotSupersession,
  useTermTimeline,
  type PolicyFileResponse,
  type TermViewModel,
} from './file/api';
import { orderTerms } from './file/model';
import { TermPremiumCard } from './file/TermPremiumCard';
import { TermTimeline } from './file/TermTimeline';
import { TransactionHistory, type HistoryGroupInput } from './file/TransactionHistory';
import { TermStatusPill } from './TermStatusPill';

/**
 * Every term of the policy, from `Policy.get` `terms[]` (known at effectiveKnownAt, by number, each with its state as
 * of the viewed date). The viewed term carries the response itself; the others are read as of their own start date
 * when their history group needs their charges.
 */
function termGroups(data: PolicyFileResponse, asOf: string): HistoryGroupInput[] {
  const terms = orderTerms(data.terms ?? (data.term ? [data.term] : []));
  return terms.map((term) =>
    term.termId === data.term?.termId
      ? { term, policy: data, validAt: asOf }
      : { term, validAt: athensToday(new Date(term.period.from)) },
  );
}

function PolicyDetails({
  data,
  asOf,
  picker,
  notice,
}: {
  data: PolicyFileResponse;
  asOf: string;
  picker: ReactNode;
  notice: ReactNode;
}) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const { policy, term } = data;
  const invoices = useInvoices({ policyId: policy.policyId });
  const groups = useMemo(() => termGroups(data, asOf), [data, asOf]);
  const timeline = useTermTimeline(policy.policyId, term?.termId ?? '', asOf, !!term);
  const snapshot = useSnapshotSupersession(policy.policyId, asOf, !!term);

  useEffect(() => {
    rememberRecent('policy', policy.policyId);
  }, [policy.policyId]);

  const allTerms = useMemo(() => groups.map((g) => g.term), [groups]);
  const renewal: TermViewModel | undefined = term
    ? allTerms.find((x) => x.termNumber > term.termNumber && x.state === 'SCHEDULED')
    : undefined;
  const status = policy.status ?? term?.state;
  const vehicle = data.riskTree?.vehicles[0];
  const drivers = data.riskTree?.drivers ?? [];
  const selectedCovers = (data.riskTree?.coverages ?? []).filter((c) => c.selected);
  const superseded = snapshot.data?.supersession?.superseded === true;

  const facts: PageFact[] = term
    ? [
        { id: 'from', label: t('term.from'), value: fmt.date(term.period.from) },
        {
          id: 'to',
          label: t('term.to'),
          value: term.period.to ? fmt.date(term.period.to) : t('term.open'),
        },
        {
          id: 'product',
          label: t('term.product'),
          value: <span className="ds-mono">{`${policy.productCode} ${term.productVersion}`}</span>,
        },
        ...(vehicle
          ? [
              {
                id: 'plate',
                label: t('risk.plate'),
                value: <span className="ds-mono">{vehicle.plate}</span>,
              },
            ]
          : []),
        ...(data.effectiveKnownAt
          ? [
              {
                id: 'knownAt',
                label: t('file.asKnownAt.label'),
                value: fmt.dateTime(data.effectiveKnownAt),
              },
            ]
          : []),
      ]
    : [];

  return (
    <>
      <PageHeader
        overline={[t('overline'), policy.productCode].join(' · ')}
        title={t('title', { number: policy.policyNumber })}
        subtitle={
          <>
            {status ? <TermStatusPill state={status} /> : null}
            {renewal ? (
              <StatusPill semantic="info" text={t('file.renewalPending')} announceChanges={false} />
            ) : null}
            {superseded ? (
              <StatusPill
                semantic="stale"
                text={t('file.superseded.badge')}
                announceChanges={false}
              />
            ) : null}
            <span className="ds-caption">{t('asOfNote', { date: fmt.date(asOf) })}</span>
          </>
        }
        facts={facts}
        actions={
          <>
            <ActionBar
              policyId={policy.policyId}
              termState={term?.state}
              renewalExists={!!renewal}
            />
            <LinkButton variant="secondary" to={`/finance/journals/policy/${policy.policyNumber}`}>
              {t('openJournal')}
            </LinkButton>
            <LinkButton variant="secondary" to={`/parties/${policy.policyholderPartyId}`}>
              {t('openPolicyholder')}
            </LinkButton>
          </>
        }
      >
        {picker}
        {term ? <TermTimeline terms={allTerms} viewed={term} /> : null}
      </PageHeader>
      {notice}
      {term ? null : (
        <Banner variant="info" title={t('noTermTitle')}>
          {t('noTermBody', { date: fmt.date(asOf) })}
        </Banner>
      )}
      {superseded ? (
        <Banner variant="warning" live="none" title={t('file.superseded.title')}>
          {snapshot.data?.supersession?.supersededAt
            ? t('file.superseded.bodyAt', {
                date: fmt.dateTime(snapshot.data.supersession.supersededAt),
              })
            : t('file.superseded.body')}
        </Banner>
      ) : null}
      <div className={styles.split}>
        <div className={styles.stack}>
          <Section
            title={t('file.history.title')}
            meta={t('file.history.meta')}
            count={groups.length}
          >
            <TransactionHistory policyId={policy.policyId} groups={[...groups].reverse()} />
          </Section>
          <Section title={t('invoices.title')}>
            <QueryView query={invoices}>
              {(page) => <InvoiceTable items={page.items} label={t('invoices.title')} compact />}
            </QueryView>
          </Section>
          <div className={styles.grid}>
            <Section title={t('term.title')} family={status === 'IN_FORCE' ? 'success' : 'brand'}>
              {term ? (
                <KeyValueList
                  aria-label={t('term.title')}
                  items={[
                    { id: 'number', label: t('term.number'), value: String(term.termNumber) },
                    { id: 'from', label: t('term.from'), value: fmt.date(term.period.from) },
                    {
                      id: 'to',
                      label: t('term.to'),
                      value: term.period.to ? fmt.date(term.period.to) : t('term.open'),
                    },
                    {
                      id: 'written',
                      label: t('term.written'),
                      value: fmt.date(term.writtenDate),
                    },
                    {
                      id: 'product',
                      label: t('term.product'),
                      value: `${policy.productCode} ${term.productVersion}`,
                      kind: 'mono',
                    },
                    {
                      id: 'plan',
                      label: t('term.plan'),
                      value: term.paymentPlanRef,
                      kind: 'mono',
                    },
                    { id: 'currency', label: t('term.currency'), value: term.currency },
                  ]}
                />
              ) : (
                <p className={styles.muted}>{t('term.none')}</p>
              )}
            </Section>
            <Section title={t('risk.title')}>
              {vehicle ? (
                <KeyValueList
                  aria-label={t('risk.title')}
                  items={[
                    { id: 'plate', label: t('risk.plate'), value: vehicle.plate, kind: 'mono' },
                    {
                      id: 'vehicle',
                      label: t('risk.vehicle'),
                      value: [vehicle.make, vehicle.model].filter(Boolean).join(' '),
                    },
                    {
                      id: 'year',
                      label: t('risk.year'),
                      value: vehicle.firstRegistrationYear
                        ? String(vehicle.firstRegistrationYear)
                        : null,
                    },
                    {
                      id: 'driver',
                      label: t('risk.drivers'),
                      value:
                        drivers.length > 0
                          ? t('risk.driverCount', { count: drivers.length })
                          : null,
                    },
                  ]}
                />
              ) : (
                <p className={styles.muted}>{t('risk.none')}</p>
              )}
            </Section>
            <Section title={t('covers.title')} count={selectedCovers.length}>
              {selectedCovers.length > 0 ? (
                <KeyValueList
                  aria-label={t('covers.title')}
                  items={selectedCovers.map((c) => ({
                    id: c.coverageCode,
                    label: c.coverageCode,
                    value: t('covers.included'),
                  }))}
                />
              ) : (
                <p className={styles.muted}>{t('covers.none')}</p>
              )}
            </Section>
          </div>
        </div>
        <div className={styles.stack}>
          {term ? (
            <Section title={t('file.premium.title')} family="success">
              <TermPremiumCard
                policyId={policy.policyId}
                term={term}
                transactions={timeline.data?.transactions}
              />
            </Section>
          ) : null}
        </div>
      </div>
    </>
  );
}

/**
 * The policy file («Φάκελος ασφαλιστηρίου»), laid out like the claim file: record header with the term
 * timeline in it, then the transaction history on the wide column and the term premium card and invoices on the
 * side column. The «as of» date goes to the API in date form, which means close of business of that day in Athens
 * (D-SLC-13); «as known at» is read-only and comes from the server's `effectiveKnownAt` (D-SL3-03).
 */
export function PolicyViewPage() {
  const { policyId = '' } = useParams();
  const { t } = useTranslation('policy');
  const [chosen, setChosen] = useState<string | null>(null);
  const today = athensToday();
  const current = usePolicyAt(policyId, today, { keepPrevious: true });
  // A policy that has not started yet shows nothing as of today (no segment, no risk). Until the user picks a date,
  // read it as of the start of its upcoming term instead, and say so.
  const upcomingStart =
    chosen === null && current.data?.policy.status === 'SCHEDULED' && current.data.term
      ? athensToday(new Date(current.data.term.period.from))
      : null;
  const asOf = chosen ?? upcomingStart ?? today;
  const query = usePolicyAt(policyId, asOf, { keepPrevious: true });
  const picker = (
    <div className={styles.asOf}>
      <DatePicker
        label={t('asOf.label')}
        description={t('asOf.help')}
        value={parseDate(asOf)}
        onChange={(value) => {
          if (value) setChosen(value.toString());
        }}
      />
    </div>
  );
  const notice = upcomingStart ? (
    <Banner variant="info" title={t('upcoming.title')}>
      {t('upcoming.body')}
    </Banner>
  ) : null;
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('notFound')}>
        {(data) => <PolicyDetails data={data} asOf={asOf} picker={picker} notice={notice} />}
      </QueryView>
    </div>
  );
}
