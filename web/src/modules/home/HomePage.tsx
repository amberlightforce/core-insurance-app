import { FilePlus2, FileSearch, LogIn, Search, ShieldAlert, UserPlus } from 'lucide-react';
import type { LucideIcon } from 'lucide-react';
import { useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-aria-components';
import { useNavigate } from 'react-router';

import {
  Button,
  KpiTile,
  StatusPill,
  WorkLeftCard,
  type ButtonVariant,
  type StatusFamily,
  type WorkLeftItem,
} from '../../design-system';
import type { ApprovalView } from '../../api/types';
import { useDevSession } from '../../dev-auth/devAuth';
import { firstName, isKnownRole, roleKeys, sampleEntityName } from '../../app-shell/user';
import { InvoiceStatePill } from '../billing/InvoiceStatePill';
import { InvoiceTable } from '../billing/InvoiceTable';
import { approvalTypeKey } from '../claims/approvalFormat';
import { cx } from '../../design-system/utils/cx';
import { athensToday } from '../quote/time';
import { Section } from '../staff/PageHeader';
import { readRecent, type RecentRecord } from '../staff/recent';
import staff from '../staff/staff.module.css';
import { useFormat } from '../staff/useFormat';
import { usePendingApprovals, useAllInvoices } from './api';
import styles from './HomePage.module.css';
import { greetingKey, summariseInvoices } from './summary';

const has = (roles: readonly string[], ...wanted: string[]) =>
  wanted.some((role) => roles.includes(role));

interface QuickAction {
  id: string;
  label: string;
  icon: LucideIcon;
  to: string;
  variant: ButtonVariant;
}

interface RecentLink extends RecentRecord {
  kind: 'policy' | 'invoice' | 'account' | 'claim';
  to: string;
}

/** Placeholder rows while a work-left list loads (the card shows its own skeleton). */
const noItems: WorkLeftItem[] = [];

function Hero({
  overline,
  greeting,
  line,
  metric,
  aside,
}: {
  overline: string;
  greeting: string;
  line: string;
  metric?: ReactNode;
  aside: ReactNode;
}) {
  return (
    <header className={styles.canopy}>
      <div className={staff.canopyImagery} aria-hidden="true">
        <i />
        <i />
        <i />
      </div>
      <div className={styles.heroText}>
        <p className={staff.overline}>{overline}</p>
        <h1 className={styles.greet}>{greeting}</h1>
        <p className={styles.heroLine}>{line}</p>
        {metric}
      </div>
      <div className={styles.heroAside}>{aside}</div>
    </header>
  );
}

/**
 * Staff home (v3 mockup «Πύλη»): a canopy with the greeting, today's date and the role, the one hero number the
 * role's data supports and the quick actions; then work-left cards, KPI tiles and the latest records. Every
 * number comes from an API list or from records opened in this browser — no targets, deltas or trends are
 * invented, so cards without a real source are left out.
 */
export function HomePage() {
  const { t } = useTranslation(['home', 'shell', 'claims']);
  const navigate = useNavigate();
  const fmt = useFormat();
  const session = useDevSession();
  const roles = session?.user.roles ?? [];
  const seesInvoices = has(roles, 'Staff.Underwriter', 'Staff.Billing');
  const seesApprovals = has(roles, 'Staff.ClaimsHandler', 'Staff.ClaimsManager');
  const invoices = useAllInvoices(seesInvoices);
  const approvals = usePendingApprovals(seesApprovals);
  const today = athensToday();
  const now = useMemo(() => new Date(), []);

  const summary = useMemo(
    () => (invoices.data ? summariseInvoices(invoices.data.items, today) : null),
    [invoices.data, today],
  );
  const pending = useMemo(
    () => (approvals.data?.items as { request: ApprovalView }[] | undefined) ?? [],
    [approvals.data],
  );

  const recent: RecentLink[] = useMemo(() => {
    const kinds: RecentLink['kind'][] = ['policy', 'invoice', 'account'];
    if (seesApprovals) kinds.push('claim');
    const paths = {
      policy: '/policies/',
      invoice: '/billing/invoices/',
      account: '/billing/accounts/',
      claim: '/claims/',
    } as const;
    return kinds.flatMap((kind) =>
      readRecent(kind)
        .slice(0, 4)
        .map((r) => ({ ...r, kind, to: `${paths[kind]}${r.id}` })),
    );
  }, [seesApprovals]);

  const dateLine = new Intl.DateTimeFormat(fmt.region, {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    year: 'numeric',
    timeZone: 'Europe/Athens',
  }).format(now);
  const roleNames = roles.map((role) => (isKnownRole(role) ? t(`shell:${roleKeys[role]}`) : role));
  const waiting = seesApprovals ? pending.length : summary ? summary.open.length : null;
  const line = [dateLine, waiting !== null ? t('home:waiting', { count: waiting }) : null]
    .filter(Boolean)
    .join(' · ');

  const actions: QuickAction[] = [
    ...(has(roles, 'Staff.Underwriter')
      ? [
          {
            id: 'quote',
            label: t('home:actions.newQuote'),
            icon: FilePlus2,
            to: '/policies/quotes/new',
            variant: 'commit' as const,
          },
        ]
      : []),
    ...(seesApprovals
      ? [
          {
            id: 'fnol',
            label: t('home:actions.newFnol'),
            icon: ShieldAlert,
            to: '/claims/new',
            variant: (has(roles, 'Staff.Underwriter') ? 'secondary' : 'commit') as ButtonVariant,
          },
        ]
      : []),
    {
      id: 'party',
      label: t('home:actions.findParty'),
      icon: Search,
      to: '/parties',
      variant: 'secondary',
    },
    ...(has(roles, 'Staff.Underwriter')
      ? [
          {
            id: 'newParty',
            label: t('home:actions.newParty'),
            icon: UserPlus,
            to: '/parties/new',
            variant: 'secondary' as const,
          },
        ]
      : []),
    ...(has(roles, 'Staff.Finance')
      ? [
          {
            id: 'journal',
            label: t('home:actions.journals'),
            icon: FileSearch,
            to: '/finance',
            variant: 'commit' as const,
          },
        ]
      : []),
  ];

  if (!session) {
    return (
      <div className={styles.home}>
        <Hero
          overline={t('home:overlineSignedOut')}
          greeting={t(`home:${greetingKey(now)}`)}
          line={dateLine}
          aside={
            <div className={styles.quick}>
              <h2 className={styles.quickTitle}>{t('home:signedOut.title')}</h2>
              <p className={staff.muted}>{t('home:signedOut.body')}</p>
              <Button
                variant="commit"
                icon={LogIn}
                onPress={() => {
                  void navigate('/dev/sign-in');
                }}
              >
                {t('home:signedOut.signIn')}
              </Button>
            </div>
          }
        />
      </div>
    );
  }

  const name = firstName(session.user.name);
  const metric =
    seesInvoices && summary ? (
      <div className={styles.metric}>
        <span className={styles.metricLabel}>{t('home:hero.openBalance')}</span>
        <div className={styles.metricRow}>
          <span className={styles.metricValue}>
            {fmt.money({ amount: String(summary.totalOpen), currency: 'EUR' })}
          </span>
          <span className={styles.metricNote}>
            {t('home:hero.openOf', { open: summary.open.length, count: summary.count })}
          </span>
        </div>
      </div>
    ) : seesApprovals && approvals.data ? (
      <div className={styles.metric}>
        <span className={styles.metricLabel}>{t('home:hero.pendingApprovals')}</span>
        <div className={styles.metricRow}>
          <span className={styles.metricValue}>{pending.length}</span>
          <span className={styles.metricNote}>{t('home:hero.pendingNote')}</span>
        </div>
      </div>
    ) : undefined;

  const cards: {
    id: string;
    title: string;
    family: StatusFamily;
    count: number;
    items: WorkLeftItem[];
    to: string;
    loading: boolean;
    error: boolean;
    retry?: () => void;
  }[] = [];

  if (seesApprovals) {
    cards.push({
      id: 'approvals',
      title: t('home:cards.approvals'),
      family: 'plum',
      count: pending.length,
      items: pending.slice(0, 3).map(({ request }) => ({
        id: request.requestId,
        label: t(approvalTypeKey(request.type), { ns: 'claims', defaultValue: request.type }),
        meta: <span className={styles.itemMeta}>{fmt.date(request.requestedAt)}</span>,
      })),
      to: '/claims/approvals',
      loading: approvals.isPending,
      error: approvals.isError,
      retry: () => void approvals.refetch(),
    });
    const claims = readRecent('claim');
    cards.push({
      id: 'claims',
      title: t('home:cards.recentClaims'),
      family: 'brand',
      count: claims.length,
      items: claims.slice(0, 3).map((r) => ({ id: r.id, label: r.label })),
      to: '/claims',
      loading: false,
      error: false,
    });
  }
  if (seesInvoices) {
    const loading = invoices.isPending;
    const error = invoices.isError;
    const retry = () => void invoices.refetch();
    cards.push(
      {
        id: 'open',
        title: t('home:cards.openInvoices'),
        family: 'warning',
        count: summary?.open.length ?? 0,
        items: (summary?.open ?? []).slice(0, 3).map((r) => ({
          id: r.invoice.invoiceId,
          label: `${r.invoice.invoiceNumber} · ${fmt.money(r.invoice.open)}`,
          meta: <InvoiceStatePill state={r.invoice.state} />,
        })),
        to: '/billing',
        loading,
        error,
        retry,
      },
      {
        id: 'overdue',
        title: t('home:cards.overdue'),
        family: 'danger',
        count: summary?.overdue.length ?? 0,
        items: (summary?.overdue ?? []).slice(0, 3).map((r) => ({
          id: r.invoice.invoiceId,
          label: `${r.invoice.invoiceNumber} · ${fmt.money(r.invoice.open)}`,
          meta: <StatusPill semantic="overdue" announceChanges={false} />,
        })),
        to: '/billing',
        loading,
        error,
        retry,
      },
      {
        id: 'paid',
        title: t('home:cards.paid'),
        family: 'success',
        count: summary?.paid.length ?? 0,
        items: (summary?.paid ?? []).slice(0, 3).map((r) => ({
          id: r.invoice.invoiceId,
          label: `${r.invoice.invoiceNumber} · ${fmt.money(r.invoice.total)}`,
        })),
        to: '/billing',
        loading,
        error,
        retry,
      },
    );
  }
  const policies = readRecent('policy');
  if (cards.length < 4 && has(roles, 'Staff.Underwriter', 'Staff.Billing', 'Staff.Finance')) {
    cards.push({
      id: 'policies',
      title: t('home:cards.recentPolicies'),
      family: 'brand',
      count: policies.length,
      items: policies.slice(0, 3).map((r) => ({ id: r.id, label: r.label })),
      to: '/policies',
      loading: false,
      error: false,
    });
  }

  return (
    <div className={styles.home}>
      <Hero
        overline={[...roleNames, sampleEntityName].join(' · ')}
        greeting={t('home:greet', { greeting: t(`home:${greetingKey(now)}`), name })}
        line={line}
        {...(metric ? { metric } : {})}
        aside={
          <div className={styles.quick}>
            <h2 className={styles.quickTitle}>{t('home:actions.title')}</h2>
            <div className={styles.quickList}>
              {actions.map((action) => (
                <Button
                  key={action.id}
                  size="lg"
                  variant={action.variant}
                  icon={action.icon}
                  onPress={() => {
                    void navigate(action.to);
                  }}
                >
                  {action.label}
                </Button>
              ))}
            </div>
          </div>
        }
      />

      {cards.length > 0 ? (
        <section className={styles.worklist} aria-label={t('home:cards.label')}>
          {cards.map((card) => (
            <WorkLeftCard
              key={card.id}
              title={card.title}
              family={card.family}
              count={card.count}
              items={card.loading ? noItems : card.items}
              continueHref={card.to}
              headingLevel={2}
              state={card.loading ? 'loading' : card.error ? 'error' : 'ready'}
              {...(card.retry ? { onRetry: card.retry } : {})}
            />
          ))}
        </section>
      ) : null}

      {seesInvoices && summary ? (
        <section className={styles.kpis} aria-label={t('home:kpi.label')}>
          <KpiTile label={t('home:kpi.invoices')} value={summary.count} format="number" />
          <KpiTile label={t('home:kpi.billed')} value={summary.totalBilled} format="money" />
          <KpiTile label={t('home:kpi.collected')} value={summary.totalPaid} format="money" />
          <KpiTile label={t('home:kpi.open')} value={summary.totalOpen} format="money" />
        </section>
      ) : null}

      <div className={styles.split}>
        {seesInvoices ? (
          <Section title={t('home:latestInvoices')} meta={t('home:latestInvoicesMeta')}>
            {summary ? (
              <InvoiceTable items={summary.latest.slice(0, 5)} label={t('home:latestInvoices')} />
            ) : (
              <p className={staff.muted}>{t('home:loading')}</p>
            )}
          </Section>
        ) : null}
        <Section title={t('home:recent.title')} meta={t('home:recent.meta')}>
          {recent.length === 0 ? (
            <p className={staff.muted}>{t('home:recent.empty')}</p>
          ) : (
            <ul className={styles.recentList}>
              {recent.map((r) => (
                <li key={`${r.kind}-${r.id}`}>
                  <span className={styles.recentDot} data-kind={r.kind} aria-hidden="true" />
                  <Link href={r.to} className={cx(styles.recentLink)}>
                    <span className={styles.recentNumber}>{r.label}</span>
                  </Link>
                  <span className={styles.recentKind}>{t(`home:recent.kinds.${r.kind}`)}</span>
                </li>
              ))}
            </ul>
          )}
        </Section>
      </div>
    </div>
  );
}
