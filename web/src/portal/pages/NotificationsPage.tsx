import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { Link } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import { Button, EmptyState, Switch, announce } from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { QueryView } from '../ui/QueryView';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import rows from '../ui/pages.module.css';
import styles from '../ui/portal.module.css';
import {
  listNotifications,
  listSubscriptions,
  markNotificationsRead,
  portalKeys,
  setSubscription,
  type NotificationClass,
  type NotificationItem,
} from './portalApi';

const classes: NotificationClass[] = ['RENEWAL', 'BILLING', 'DOCUMENT', 'MESSAGE', 'CLAIM'];

/** Where a notification of a class leads. */
const destinations: Record<NotificationClass, string | null> = {
  RENEWAL: '/portal/policies',
  BILLING: '/portal/billing',
  DOCUMENT: '/portal/documents',
  MESSAGE: '/portal/messages',
  CLAIM: null,
};

function Preferences() {
  const { t } = useTranslation('portal');
  const { keyFor, release } = useIdempotencyKey();
  const client = useQueryClient();
  const query = useQuery({ queryKey: portalKeys.subscriptions, queryFn: listSubscriptions });
  return (
    <PortalSection title={t('ntf.preferences')} family="plum">
      <p className={styles.muted}>{t('ntf.preferencesLead')}</p>
      <QueryView query={query} errorTitle={t('ntf.loadFailed')}>
        {(data) => (
          <ul className={rows.rows}>
            {classes.map((c) => {
              // No row for a class means the default, which is on.
              const enabled =
                data.subscriptions.find((s) => s.notificationClass === c)?.enabled ?? true;
              return (
                <li key={c} className={rows.row}>
                  <Switch
                    isSelected={enabled}
                    labelText={t(`ntf.class.${c}`)}
                    onChangeAsync={(next) =>
                      setSubscription(c, next, keyFor({ c, next })).then((result) => {
                        release();
                        client.setQueryData(portalKeys.subscriptions, result);
                        announce(t('ntf.saved'));
                      })
                    }
                  >
                    {t(`ntf.class.${c}`)}
                  </Switch>
                </li>
              );
            })}
          </ul>
        )}
      </QueryView>
    </PortalSection>
  );
}

function Row({ item, onRead }: { item: NotificationItem; onRead: (id: string) => void }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  const unread = item.readAt === undefined;
  const to = destinations[item.notificationClass];
  return (
    <li className={`${rows.row} ${unread ? rows.unread : ''}`}>
      <div className={rows.rowMain}>
        <p className={rows.rowTitle}>{t(`ntf.kind.${item.kind}`)}</p>
        <p className={rows.rowMeta}>
          {fmt.dateTime(item.createdAt)}
          {unread ? ` · ${t('ntf.new')}` : ''}
        </p>
      </div>
      <div className={rows.rowSide}>
        {to ? (
          <Link
            className={styles.textLink}
            to={to}
            onClick={() => unread && onRead(item.notificationId)}
          >
            {t('ntf.open')}
          </Link>
        ) : null}
        {unread ? (
          <Button
            variant="ghost"
            size="sm"
            aria-label={t('ntf.markReadOne', { what: t(`ntf.kind.${item.kind}`) })}
            onPress={() => {
              onRead(item.notificationId);
            }}
          >
            {t('ntf.markRead')}
          </Button>
        ) : null}
      </div>
    </li>
  );
}

/** `/portal/notifications`: the inbox (mark read) and which kinds of notice the customer wants. */
export function NotificationsPage() {
  const { t } = useTranslation('portal');
  usePortalTitle(t('nav.notifications'));
  const client = useQueryClient();
  const { keyFor, release } = useIdempotencyKey();
  const query = useQuery({ queryKey: portalKeys.notifications, queryFn: listNotifications });
  const mark = useMutation({
    mutationFn: (body: { notificationIds: string[] } | { all: true }) =>
      markNotificationsRead(body, keyFor(body)),
    onSuccess: () => {
      release();
      void client.invalidateQueries({ queryKey: portalKeys.all });
    },
  });

  return (
    <PortalPage>
      <PortalHero overline={t('ntf.overline')} title={t('ntf.title')} lead={t('ntf.lead')} />
      <QueryView query={query} errorTitle={t('ntf.loadFailed')}>
        {(page) => {
          const unread = page.items.filter((n) => n.readAt === undefined).length;
          return page.items.length === 0 ? (
            <EmptyState
              kind="done"
              headingLevel={2}
              headline={t('ntf.empty')}
              description={t('ntf.emptyBody')}
            />
          ) : (
            <PortalSection title={t('ntf.inbox', { count: unread })} family="info">
              {mark.isError ? (
                <PortalProblem error={mark.error} title={t('ntf.markFailed')} />
              ) : null}
              {unread > 0 ? (
                <div className={styles.actions}>
                  <Button
                    variant="secondary"
                    size="sm"
                    isLoading={mark.isPending}
                    onPress={() => {
                      mark.mutate({ all: true });
                    }}
                  >
                    {t('ntf.markAll')}
                  </Button>
                </div>
              ) : null}
              <ul className={rows.rows}>
                {page.items.map((n) => (
                  <Row
                    key={n.notificationId}
                    item={n}
                    onRead={(id) => {
                      mark.mutate({ notificationIds: [id] });
                    }}
                  />
                ))}
              </ul>
            </PortalSection>
          );
        }}
      </QueryView>
      <Preferences />
    </PortalPage>
  );
}
