import { Bell, CheckCheck, type LucideIcon } from 'lucide-react';
import { useRef, useState, type ReactNode, type Ref } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx, defined } from '../../utils/cx';
import { Button } from '../Button';
import { Drawer } from '../Drawer';
import styles from './Notifications.module.css';
import { useSeen } from './useSeen';

export interface NotificationBellProps {
  /** New (unseen) notifications; the badge caps at «99+». */
  count: number;
  /** Any action-required item: the badge uses `--color-badge-attention-bg`, otherwise neutral. */
  attention?: boolean;
  /**
   * Bump this number when an urgent item arrives (breached clock, assigned sanctions hit, conflict on a
   * record being edited): the bell swings once (MI-26). Nothing else swings it.
   */
  urgentSignal?: number;
  /** The centre is open (`aria-expanded`). */
  isExpanded?: boolean;
  onPress?: () => void;
  ref?: Ref<HTMLButtonElement>;
}

/**
 * Notification bell (Part 2 §4.39): 20 px `bell` icon button with a count badge, named
 * «Ειδοποιήσεις, {count} νέες». MI-26 swing only for urgent items (reduced motion: badge only).
 */
export function NotificationBell({
  count,
  attention = false,
  urgentSignal = 0,
  isExpanded,
  onPress,
  ref,
}: NotificationBellProps) {
  const { t } = useTranslation('ds');
  const [lastSignal, setLastSignal] = useState(urgentSignal);
  const [swing, setSwing] = useState(0);
  if (urgentSignal !== lastSignal) {
    setLastSignal(urgentSignal);
    if (urgentSignal > lastSignal) setSwing((value) => value + 1);
  }
  const badge = count > 99 ? t('notifications.cap') : String(count);

  return (
    <AriaButton
      ref={ref}
      className={cx(styles.bell)}
      aria-label={t('notifications.bell', { count })}
      {...defined({ onPress, 'aria-expanded': isExpanded })}
    >
      <span
        key={swing}
        className={styles.bellIcon}
        data-swing={swing > 0 || undefined}
        aria-hidden="true"
      >
        <Icon icon={Bell} size={20} />
      </span>
      {count > 0 ? (
        <span
          key={`badge-${String(swing)}`}
          className={styles.badge}
          data-attention={attention || undefined}
          data-roll={swing > 0 || undefined}
          aria-hidden="true"
        >
          {badge}
        </span>
      ) : null}
    </AriaButton>
  );
}

export type NotificationFamily = 'danger' | 'warning' | 'info' | 'success' | 'neutral' | 'brand';

export interface NotificationItem {
  id: string;
  title: string;
  /** Two-line body. */
  body?: string;
  /** Already formatted («πριν 5 λεπτά», «14:02»). */
  time: string;
  unread: boolean;
  family?: NotificationFamily;
  icon?: LucideIcon;
  /** Inline actions («Έγκριση», «Άνοιγμα», «Αναβολή 1 ώρα»). */
  actions?: ReactNode;
}

function NotificationRow({
  item,
  isActive,
  onSeen,
}: {
  item: NotificationItem;
  isActive: boolean;
  onSeen?: (id: string) => void;
}) {
  const { t } = useTranslation('ds');
  const ref = useRef<HTMLLIElement | null>(null);
  const seen = useSeen(ref, isActive && item.unread, () => onSeen?.(item.id));
  const unread = item.unread && !seen;
  return (
    <li
      ref={ref}
      className={styles.item}
      data-unread={item.unread || undefined}
      data-seen={seen || undefined}
    >
      <span className={styles.tile} data-family={item.family ?? 'neutral'} aria-hidden="true">
        {item.icon ? <Icon icon={item.icon} size={20} /> : <Icon icon={Bell} size={20} />}
      </span>
      <div className={styles.content}>
        <p className={styles.title}>
          {item.unread ? (
            <span className="ds-visually-hidden">{t('notifications.unread')}: </span>
          ) : null}
          {item.title}
        </p>
        {item.body ? <p className={styles.body}>{item.body}</p> : null}
        <p className={styles.time}>{item.time}</p>
        {item.actions ? <div className={styles.actions}>{item.actions}</div> : null}
      </div>
      {item.unread ? (
        <span className={styles.dot} data-fading={!unread || undefined} aria-hidden="true" />
      ) : null}
    </li>
  );
}

export interface NotificationCenterProps {
  isOpen: boolean;
  onOpenChange: (isOpen: boolean) => void;
  notifications: NotificationItem[];
  onMarkAllRead?: () => void;
  /** Called when an unread item has been visible for 1 s (MI-60). */
  onSeen?: (id: string) => void;
  /** Tabs or filters above the list («Για ενέργεια», «Ενημερώσεις», «Αναφορές»). */
  toolbar?: ReactNode;
}

/**
 * Notification centre (Part 2 §4.39, DESIGN-B A.10): a 400 px non-modal drawer (complementary landmark,
 * F6, Esc) listing notifications with an unread dot that fades after 1 s in view (MI-60), «Σήμανση όλων ως
 * αναγνωσμένων», and the empty state «Δεν υπάρχουν ειδοποιήσεις».
 */
export function NotificationCenter({
  isOpen,
  onOpenChange,
  notifications,
  onMarkAllRead,
  onSeen,
  toolbar,
}: NotificationCenterProps) {
  const { t } = useTranslation('ds');
  const anyUnread = notifications.some((item) => item.unread);
  return (
    <Drawer
      title={t('notifications.title')}
      isOpen={isOpen}
      onOpenChange={onOpenChange}
      isModal={false}
      width={400}
      headerActions={
        onMarkAllRead && anyUnread ? (
          <Button
            variant="ghost"
            size="sm"
            icon={CheckCheck}
            label={t('notifications.markAllRead')}
            onPress={onMarkAllRead}
          />
        ) : undefined
      }
    >
      {toolbar}
      {notifications.length === 0 ? (
        <p className={styles.empty}>{t('notifications.empty')}</p>
      ) : (
        <ul className={styles.list}>
          {notifications.map((item) => (
            <NotificationRow
              key={item.id}
              item={item}
              isActive={isOpen}
              {...(onSeen ? { onSeen } : {})}
            />
          ))}
        </ul>
      )}
    </Drawer>
  );
}
