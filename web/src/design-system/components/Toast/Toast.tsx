import { CircleAlert, CircleCheck, Info, TriangleAlert, X, type LucideIcon } from 'lucide-react';
import {
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
  type CSSProperties,
  type RefObject,
} from 'react';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { useReducedMotion } from '../../preferences/context';
import { durations } from '../../tokens';
import { Button } from '../Button';
import { ProgressBar } from '../Progress';
import { Spinner } from '../Spinner';
import styles from './Toast.module.css';
import {
  maxVisibleToasts,
  toast,
  toastStore,
  type ToastFamily,
  type ToastRecord,
} from './toastStore';

const icons: Record<Exclude<ToastFamily, 'progress'>, LucideIcon> = {
  success: CircleCheck,
  info: Info,
  warning: TriangleAlert,
  error: CircleAlert,
};

interface ToastItemProps {
  record: ToastRecord;
  depth: number;
  paused: boolean;
  onClosed: (id: string, viaKeyboard: boolean) => void;
}

function ToastItem({ record, depth, paused, onClosed }: ToastItemProps) {
  const { t } = useTranslation('ds');
  const reducedMotion = useReducedMotion();
  const [exiting, setExiting] = useState(false);
  const remaining = useRef(record.duration);
  const viaKeyboard = useRef(false);

  const lastRevision = useRef(record.revision);
  const revision = record.revision;
  const duration = record.duration;

  useEffect(() => {
    // Restart the timer when the toast changes family (a progress toast resolving).
    if (lastRevision.current !== revision) {
      lastRevision.current = revision;
      remaining.current = duration;
    }
    if (exiting) {
      const id = setTimeout(
        () => {
          onClosed(record.id, viaKeyboard.current);
        },
        reducedMotion ? 0 : durations.transitionSm,
      );
      return () => {
        clearTimeout(id);
      };
    }
    const budget = remaining.current;
    if (paused || budget === null) return undefined;
    const startedAt = Date.now();
    const id = setTimeout(() => {
      setExiting(true);
    }, budget);
    return () => {
      clearTimeout(id);
      remaining.current = Math.max(0, budget - (Date.now() - startedAt));
    };
  }, [paused, exiting, revision, duration, record.id, onClosed, reducedMotion]);

  const close = (keyboard: boolean) => {
    viaKeyboard.current = keyboard;
    setExiting(true);
  };

  const alert = record.family === 'warning' || record.family === 'error';
  const hairline = {
    animationDuration: record.duration === null ? undefined : `${String(record.duration)}ms`,
  } as CSSProperties;

  return (
    <li
      className={styles.toast}
      data-family={record.family}
      data-depth={depth}
      data-exiting={exiting || undefined}
      tabIndex={-1}
      aria-labelledby={`${record.id}-title`}
      onKeyDown={(event) => {
        if (event.key === 'Escape') {
          event.stopPropagation();
          close(true);
        }
      }}
    >
      <div className={styles.card} data-material="float" {...(alert ? { role: 'alert' } : {})}>
        <span className={styles.icon} aria-hidden="true">
          {record.family === 'progress' ? (
            <Spinner size={16} delayed={false} label={null} />
          ) : (
            <Icon icon={icons[record.family]} size={16} />
          )}
        </span>
        <div className={styles.text}>
          <p id={`${record.id}-title`} className={styles.title}>
            {record.title}
          </p>
          {record.description ? <p className={styles.description}>{record.description}</p> : null}
          {record.family === 'progress' ? (
            <ProgressBar
              aria-label={record.title}
              size="sm"
              showValue={false}
              className={cx(styles.progress)}
              {...(typeof record.progress === 'number'
                ? { value: record.progress, maxValue: 1 }
                : { isIndeterminate: true })}
            />
          ) : null}
        </div>
        {record.action ? (
          <Button
            variant="link"
            size="sm"
            onPress={() => {
              record.action?.onAction();
              close(false);
            }}
          >
            {record.action.label}
          </Button>
        ) : null}
        <Button
          variant="ghost"
          size="sm"
          icon={X}
          label={t('toast.dismiss')}
          onPress={() => {
            close(false);
          }}
        />
        {record.duration !== null ? (
          <span
            key={revision}
            className={styles.hairline}
            data-paused={paused || undefined}
            style={hairline}
            aria-hidden="true"
          />
        ) : null}
      </div>
    </li>
  );
}

function useWindowBlurred(): boolean {
  const [blurred, setBlurred] = useState(false);
  useEffect(() => {
    const onBlur = () => {
      setBlurred(true);
    };
    const onFocus = () => {
      setBlurred(false);
    };
    window.addEventListener('blur', onBlur);
    window.addEventListener('focus', onFocus);
    return () => {
      window.removeEventListener('blur', onBlur);
      window.removeEventListener('focus', onFocus);
    };
  }, []);
  return blurred;
}

function focusNewest(listRef: RefObject<HTMLOListElement | null>): boolean {
  const items = listRef.current?.querySelectorAll<HTMLElement>(':scope > li');
  const newest = items?.[items.length - 1];
  newest?.focus();
  return newest !== undefined;
}

/**
 * Toast region (Part 2 §4.21): one per app, bottom-right above the status bar. `role=region` labelled
 * «Ειδοποιήσεις»; success/info are announced politely, warning/error are `role=alert`. Up to 3 toasts form
 * a depth stack that expands on hover or focus; later ones queue. Timers pause on hover, focus within
 * and while the window is blurred (WCAG 2.2.1). F8 focuses the newest toast; Esc dismisses the focused one.
 *
 * We render our own region rather than RAC's UNSTABLE_ToastRegion: the guide needs per-family live
 * semantics (polite vs alert), window-blur pausing, the depth stack and progress toasts that resolve in
 * place, which the unstable API does not cover.
 */
export function Toaster() {
  const { t } = useTranslation('ds');
  const records = useSyncExternalStore(toastStore.subscribe, toastStore.getSnapshot);
  const [hovered, setHovered] = useState(false);
  const [focusWithin, setFocusWithin] = useState(false);
  const windowBlurred = useWindowBlurred();
  const listRef = useRef<HTMLOListElement | null>(null);
  const returnFocus = useRef<HTMLElement | null>(null);
  const visible = records.slice(0, maxVisibleToasts);
  const paused = hovered || focusWithin || windowBlurred;

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'F8' || event.altKey || event.ctrlKey || event.metaKey) return;
      const active = document.activeElement;
      if (!listRef.current?.contains(active) && active instanceof HTMLElement) {
        returnFocus.current = active;
      }
      if (focusNewest(listRef)) event.preventDefault();
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, []);

  const onClosed = (id: string, viaKeyboard: boolean) => {
    toast.dismiss(id);
    if (!viaKeyboard) return;
    // Keep keyboard users in the region, or send them back where they were.
    requestAnimationFrame(() => {
      if (!focusNewest(listRef)) returnFocus.current?.focus();
    });
  };

  return (
    <section
      className={styles.region}
      aria-label={t('toast.region')}
      data-expanded={hovered || focusWithin || undefined}
      onPointerEnter={() => {
        setHovered(true);
      }}
      onPointerLeave={() => {
        setHovered(false);
      }}
      onFocus={() => {
        setFocusWithin(true);
      }}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) setFocusWithin(false);
      }}
    >
      <ol ref={listRef} className={styles.list}>
        {visible.map((record, index) => (
          <ToastItem
            key={record.id}
            record={record}
            depth={visible.length - 1 - index}
            paused={paused}
            onClosed={onClosed}
          />
        ))}
      </ol>
    </section>
  );
}
