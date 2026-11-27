import { Undo2 } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';

import { EmptyState, Icon, StatusPill } from '../../design-system';
import { PortalHero, PortalPage, PortalSection } from '../ui/PortalUi';
import { QueryView } from '../ui/QueryView';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import { portalNav, withdrawalPath } from '../ui/nav';
import styles from '../ui/portal.module.css';
import rows from '../ui/pages.module.css';
import { usePortalDashboard, type PortalDashboard } from './portalApi';

function Figures({ data }: { data: PortalDashboard }) {
  const { t } = useTranslation('portal');
  const figures = [
    { id: 'policies', value: data.activePolicyCount, label: t('ov.activePolicies') },
    { id: 'renewals', value: data.renewalsDue.length, label: t('ov.renewalsDue') },
    { id: 'invoices', value: data.unpaidInvoices.length, label: t('ov.unpaidInvoices') },
    { id: 'unread', value: data.unreadNotificationCount, label: t('ov.unread') },
  ];
  return (
    <ul className={rows.figures} aria-label={t('ov.figures')}>
      {figures.map((f) => (
        <li key={f.id} className={rows.figure}>
          <p className={rows.figureValue}>{f.value}</p>
          <p className={rows.figureLabel}>{f.label}</p>
        </li>
      ))}
    </ul>
  );
}

/** «Work left»: what the customer still has to do, from real data only. */
function WorkLeft({ data }: { data: PortalDashboard }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const nothing =
    data.renewalsDue.length === 0 &&
    data.unpaidInvoices.length === 0 &&
    data.openRequests.length === 0 &&
    data.unreadNotificationCount === 0;

  if (nothing) {
    return (
      <EmptyState
        kind="done"
        headingLevel={2}
        headline={t('ov.allDone')}
        description={t('ov.allDoneBody')}
      />
    );
  }
  return (
    <ul className={rows.rows}>
      {data.renewalsDue.map((r) => (
        <li key={`r-${r.policyId}`} className={rows.row}>
          <div className={rows.rowMain}>
            <p className={rows.rowTitle}>{t('ov.renewalTitle', { number: r.policyNumber })}</p>
            <p className={rows.rowMeta}>
              {t('ov.renewalMeta', { date: fmt.date(r.expiresAt), count: r.daysLeft })}
            </p>
          </div>
          <div className={rows.rowSide}>
            <StatusPill
              semantic={r.renewalJobId ? 'info' : 'pending-approval'}
              text={r.renewalJobId ? t('ov.offerReady') : t('ov.offerPreparing')}
            />
            <Link
              className={styles.textLink}
              to={
                r.renewalJobId
                  ? `/portal/policies/${r.policyId}/renew`
                  : `/portal/policies/${r.policyId}`
              }
            >
              {r.renewalJobId ? t('ov.reviewRenewal') : t('ov.viewPolicy')}
            </Link>
          </div>
        </li>
      ))}
      {data.unpaidInvoices.map((i) => (
        <li key={`i-${i.invoiceId}`} className={rows.row}>
          <div className={rows.rowMain}>
            <p className={rows.rowTitle}>{t('ov.invoiceTitle', { number: i.invoiceNumber })}</p>
            <p className={rows.rowMeta}>
              {t('ov.invoiceMeta', { date: fmt.date(i.dueDate), amount: fmt.money(i.open) })}
            </p>
          </div>
          <div className={rows.rowSide}>
            {i.overdue ? <StatusPill semantic="overdue" text={t('pay.overdue')} /> : null}
            <Link className={styles.textLink} to="/portal/billing">
              {t('ov.goToPayments')}
            </Link>
          </div>
        </li>
      ))}
      {data.openRequests.map((q) => (
        <li key={`q-${q.threadId}`} className={rows.row}>
          <div className={rows.rowMain}>
            <p className={rows.rowTitle}>
              {t('ov.requestTitle', { topic: t(`msg.topic.${q.topic}`) })}
            </p>
            <p className={rows.rowMeta}>
              {t('ov.requestMeta', { date: fmt.dateTime(q.lastMessageAt) })}
            </p>
          </div>
          <Link className={styles.textLink} to="/portal/messages">
            {t('ov.openMessages')}
          </Link>
        </li>
      ))}
      {data.unreadNotificationCount > 0 ? (
        <li className={rows.row}>
          <div className={rows.rowMain}>
            <p className={rows.rowTitle}>
              {t('ov.unreadTitle', { count: data.unreadNotificationCount })}
            </p>
          </div>
          <Link className={styles.textLink} to="/portal/notifications">
            {t('ov.openNotifications')}
          </Link>
        </li>
      ) : null}
    </ul>
  );
}

/** Portal home: key figures, the work left, and the way into the portal's areas. */
export function DashboardPage() {
  const { t } = useTranslation('portal');
  usePortalTitle(t('nav.overview'));
  const dashboard = usePortalDashboard();
  const shortcuts = portalNav.filter((item) => item.id !== 'overview');

  return (
    <PortalPage>
      <PortalHero
        title={t('dashboard.title')}
        overline={t('dashboard.overline')}
        lead={t('dashboard.lead')}
      />
      <QueryView query={dashboard} errorTitle={t('ov.loadFailed')}>
        {(data) =>
          data.policyCount === 0 ? (
            <EmptyState
              kind="first-use"
              headingLevel={2}
              headline={t('ov.noPolicies')}
              description={t('ov.noPoliciesBody')}
            />
          ) : (
            <>
              <Figures data={data} />
              <PortalSection title={t('ov.workLeft')} family="brand">
                <WorkLeft data={data} />
              </PortalSection>
            </>
          )
        }
      </QueryView>
      <PortalSection title={t('dashboard.shortcuts')} family="teal">
        <ul className={styles.grid}>
          {shortcuts.map((item) => (
            <li key={item.id} className={styles.gridItem}>
              <Link to={item.to} className={styles.shortcut}>
                <span className={styles.shortcutIcon} aria-hidden="true">
                  <Icon icon={item.icon} size={20} />
                </span>
                <h3 className={styles.shortcutTitle}>{t(`nav.${item.id}`)}</h3>
                <p className={styles.shortcutBody}>{t(`dashboard.shortcut.${item.id}`)}</p>
              </Link>
            </li>
          ))}
        </ul>
      </PortalSection>
      <PortalSection title={t('dashboard.withdrawalTitle')} family="plum">
        <p className={styles.muted}>{t('dashboard.withdrawalBody')}</p>
        <div className={styles.actions}>
          <Link to={withdrawalPath} className={styles.textLink}>
            <Icon icon={Undo2} size={14} /> {t('dashboard.withdrawalAction')}
          </Link>
        </div>
      </PortalSection>
    </PortalPage>
  );
}
