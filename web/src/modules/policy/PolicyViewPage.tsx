import { parseDate } from '@internationalized/date';
import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useParams } from 'react-router';

import type { ChargeLine, PolicyGetResponse } from '../../api/types';
import {
  Banner,
  DatePicker,
  KeyValueList,
  StatusPill,
  dateColumn,
  identifierColumn,
  moneyColumn,
  statusColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { InvoiceTable } from '../billing/InvoiceTable';
import { useInvoices } from '../billing/api';
import { LinkButton } from '../staff/LinkButton';
import { PageHeader, Section, type PageFact } from '../staff/PageHeader';
import { QueryView } from '../staff/QueryView';
import { rememberRecent } from '../staff/recent';
import { SimpleTable } from '../staff/SimpleTable';
import styles from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { athensToday } from '../quote/time';
import { usePolicy } from './api';
import { TermStatusPill } from './TermStatusPill';

type Transaction = PolicyGetResponse['transactions'][number];

function PolicyDetails({
  data,
  asOf,
  picker,
  notice,
}: {
  data: PolicyGetResponse;
  asOf: string;
  picker: ReactNode;
  notice: ReactNode;
}) {
  const { t } = useTranslation('policy');
  const fmt = useFormat();
  const { policy, term } = data;
  const invoices = useInvoices({ policyId: policy.policyId });

  useEffect(() => {
    rememberRecent('policy', policy.policyId);
  }, [policy.policyId]);

  const sequenceOf = useMemo(
    () => new Map(data.transactions.map((x) => [x.transactionId, x] as const)),
    [data.transactions],
  );

  const transactionColumns = useMemo<DataColumn<Transaction>[]>(
    () => [
      textColumn<Transaction>(
        'sequence',
        t('transactions.columns.sequence'),
        (r) => String(r.sequence),
        { size: 90 },
      ),
      textColumn<Transaction>('kind', t('transactions.columns.kind'), (r) => r.kind),
      dateColumn<Transaction>(
        'effective',
        t('transactions.columns.effective'),
        (r) => r.effectiveAt,
      ),
      dateColumn<Transaction>('recorded', t('transactions.columns.recorded'), (r) => r.recordedAt),
      moneyColumn<Transaction>(
        'premium',
        t('transactions.columns.premium'),
        (r) => r.premium.amount,
      ),
      moneyColumn<Transaction>('taxes', t('transactions.columns.taxes'), (r) => r.taxes.amount),
      moneyColumn<Transaction>('total', t('transactions.columns.total'), (r) => r.total.amount),
    ],
    [t],
  );
  const chargeColumns = useMemo<DataColumn<ChargeLine>[]>(
    () => [
      textColumn<ChargeLine>('transaction', t('charges.columns.transaction'), (c) => {
        const x = c.transactionId ? sequenceOf.get(c.transactionId) : undefined;
        return x ? `${String(x.sequence)} · ${x.kind}` : null;
      }),
      textColumn<ChargeLine>('coverage', t('charges.columns.coverage'), (c) => c.coverageCode),
      identifierColumn<ChargeLine>('chargeType', t('charges.columns.charge'), (c) => c.chargeType, {
        size: 160,
      }),
      textColumn<ChargeLine>('category', t('charges.columns.category'), (c) => c.chargeCategory),
      statusColumn<ChargeLine>(
        'legal',
        t('charges.columns.legalStatus'),
        (c) => c.legalStatus ?? null,
        (c) =>
          c.provisional === true ? (
            <StatusPill
              semantic="warning"
              subLabel={t('charges.provisional')}
              announceChanges={false}
            />
          ) : c.legalStatus ? (
            <span>{c.legalStatus}</span>
          ) : null,
        { size: 240 },
      ),
      moneyColumn<ChargeLine>('amount', t('charges.columns.amount'), (c) => c.amount.amount, {
        currency: 'EUR',
      }),
    ],
    [t, sequenceOf],
  );

  const status = policy.status ?? term?.state;
  const vehicle = data.riskTree?.vehicles[0];
  const drivers = data.riskTree?.drivers ?? [];
  const selectedCovers = (data.riskTree?.coverages ?? []).filter((c) => c.selected);

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
            <span className="ds-caption">{t('asOfNote', { date: fmt.date(asOf) })}</span>
          </>
        }
        facts={facts}
        actions={
          <>
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
      </PageHeader>
      {notice}
      {term ? null : (
        <Banner variant="info" title={t('noTermTitle')}>
          {t('noTermBody', { date: fmt.date(asOf) })}
        </Banner>
      )}
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
                { id: 'written', label: t('term.written'), value: fmt.date(term.writtenDate) },
                {
                  id: 'product',
                  label: t('term.product'),
                  value: `${policy.productCode} ${term.productVersion}`,
                  kind: 'mono',
                },
                { id: 'plan', label: t('term.plan'), value: term.paymentPlanRef, kind: 'mono' },
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
                    drivers.length > 0 ? t('risk.driverCount', { count: drivers.length }) : null,
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
      <Section title={t('transactions.title')} count={data.transactions.length}>
        <SimpleTable<Transaction>
          aria-label={t('transactions.title')}
          columns={transactionColumns}
          data={data.transactions}
          getRowId={(r) => r.transactionId}
        />
      </Section>
      <Section title={t('charges.title')}>
        <SimpleTable<ChargeLine>
          aria-label={t('charges.title')}
          columns={chargeColumns}
          data={data.charges}
          getRowId={(c) =>
            c.chargeId ?? `${c.coverageCode}:${c.chargeType}:${c.transactionId ?? ''}`
          }
        />
      </Section>
      <Section title={t('invoices.title')}>
        <QueryView query={invoices}>
          {(page) => <InvoiceTable items={page.items} label={t('invoices.title')} />}
        </QueryView>
      </Section>
    </>
  );
}

/**
 * Policy view with an «as of» date (validAt). The date goes to the API in date form, which means close of
 * business of that day in Athens (D-SLC-13); the status shown (Scheduled, In force, Expired) is the one at that date.
 * Layout: the record sheet (v3 mockup «Φάκελος»): header strip with the key facts and the as-of picker, then
 * the money tables on the wide column and the term, vehicle and covers on the side column.
 */
export function PolicyViewPage() {
  const { policyId = '' } = useParams();
  const { t } = useTranslation('policy');
  const [chosen, setChosen] = useState<string | null>(null);
  const today = athensToday();
  const current = usePolicy(policyId, today);
  // A policy that has not started yet shows nothing as of today (no segment, no risk). Until the user picks a date,
  // read it as of the start of its upcoming term instead, and say so.
  const upcomingStart =
    chosen === null && current.data?.policy.status === 'SCHEDULED' && current.data.term
      ? athensToday(new Date(current.data.term.period.from))
      : null;
  const asOf = chosen ?? upcomingStart ?? today;
  const query = usePolicy(policyId, asOf);
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
