import { ArrowLeft } from 'lucide-react';
import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import { Button } from '../../components/Button';
import { usePreferences } from '../../preferences';
import { cx } from '../../utils/cx';
import { clampWidth, shouldStack, splitDefaults, widthForKey } from './splitMath';
import styles from './SplitView.module.css';

export interface SplitViewProps {
  /** Accessible name of the list region, e.g. «Οι παραπομπές μου». */
  listLabel: string;
  detailLabel: string;
  list: ReactNode;
  detail: ReactNode;
  /** In the stacked layout the detail covers the list while an item is open. */
  isDetailOpen: boolean;
  /** Back from the stacked detail (Esc or Alt+← also call it). */
  onCloseDetail: () => void;
  /** Initial list width in px (defaults per density: 420 / 460). */
  defaultListWidth?: number;
  /** Persist the width under this key (per user, localStorage until the PLT profile exists). */
  storageKey?: string;
}

function readStoredWidth(key: string | undefined): number | null {
  if (!key) return null;
  try {
    const raw = localStorage.getItem(key);
    return raw ? clampWidth(Number(raw)) : null;
  } catch {
    return null;
  }
}

/**
 * Workbench split view (IB-05): list pane 420/460 px resizable 320–640 through a keyboard-operable
 * separator (`role="separator"` with aria-valuenow; ←/→ 16 px, Shift 64 px, Home/End). When the detail would be
 * narrower than 560 px the panes stack and an open item covers the list, with a «Πίσω» button, Esc and Alt+←.
 */
export function SplitView({
  listLabel,
  detailLabel,
  list,
  detail,
  isDetailOpen,
  onCloseDetail,
  defaultListWidth,
  storageKey,
}: SplitViewProps) {
  const { t } = useTranslation('ds');
  const { preferences } = usePreferences();
  const densityDefault =
    preferences.density === 'comfortable' ? splitDefaults.comfortableWidth : splitDefaults.compactWidth;
  const [width, setWidth] = useState(() => readStoredWidth(storageKey) ?? defaultListWidth ?? densityDefault);
  const [containerWidth, setContainerWidth] = useState(0);
  const containerRef = useRef<HTMLDivElement>(null);
  const drag = useRef<{ startX: number; startWidth: number } | null>(null);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (entry) setContainerWidth(entry.contentRect.width);
    });
    observer.observe(el);
    return () => {
      observer.disconnect();
    };
  }, []);

  useEffect(() => {
    if (!storageKey) return;
    try {
      localStorage.setItem(storageKey, String(width));
    } catch {
      // Not persisted this session.
    }
  }, [storageKey, width]);

  const stacked = shouldStack(containerWidth, width);

  const onSeparatorKey = (event: KeyboardEvent<HTMLDivElement>) => {
    const next = widthForKey(event.key, event.shiftKey, width);
    if (next === null) return;
    event.preventDefault();
    setWidth(next);
  };

  const onPointerDown = (event: PointerEvent<HTMLDivElement>) => {
    drag.current = { startX: event.clientX, startWidth: width };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const onPointerMove = (event: PointerEvent<HTMLDivElement>) => {
    if (!drag.current) return;
    setWidth(clampWidth(drag.current.startWidth + event.clientX - drag.current.startX));
  };
  const onPointerUp = () => {
    drag.current = null;
  };

  const onDetailKey = (event: KeyboardEvent<HTMLElement>) => {
    if (!stacked) return;
    if (event.key === 'Escape' || (event.altKey && event.key === 'ArrowLeft')) {
      event.preventDefault();
      onCloseDetail();
    }
  };

  const showList = !stacked || !isDetailOpen;
  const showDetail = !stacked || isDetailOpen;

  return (
    <div
      ref={containerRef}
      className={cx(styles.split)}
      data-stacked={stacked || undefined}
      style={stacked ? undefined : { gridTemplateColumns: `${String(width)}px auto minmax(0, 1fr)` }}
    >
      <section className={cx(styles.list)} aria-label={listLabel} hidden={!showList}>
        {list}
      </section>
      {!stacked ? (
        <div
          role="separator"
          tabIndex={0}
          aria-orientation="vertical"
          aria-label={t('splitView.resize')}
          aria-valuenow={width}
          aria-valuemin={splitDefaults.min}
          aria-valuemax={splitDefaults.max}
          className={cx(styles.separator)}
          onKeyDown={onSeparatorKey}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
        />
      ) : null}
      <section className={cx(styles.detail)} aria-label={detailLabel} hidden={!showDetail} onKeyDown={onDetailKey}>
        {stacked && isDetailOpen ? (
          <div className={cx(styles.back)}>
            <Button variant="ghost" size="sm" icon={ArrowLeft} shortcut="Alt+←" onPress={onCloseDetail}>
              {t('splitView.back')}
            </Button>
          </div>
        ) : null}
        {detail}
      </section>
    </div>
  );
}
