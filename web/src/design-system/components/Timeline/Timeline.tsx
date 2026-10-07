import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Label, RadioButton, RadioField, RadioGroup } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import { ErrorState, SkeletonBlock } from '../States';
import styles from './Timeline.module.css';
import { TimelineItem } from './TimelineItem';
import {
  dayLabel,
  filterEvents,
  groupTimeline,
  type TimelineEvent,
  type TimelineFilter,
} from './timelineModel';
import { useNow } from './useNow';

export interface TimelineProps {
  /** Accessible name of the feed, e.g. «Ιστορικό ζημίας». */
  'aria-label': string;
  /** Newest first. Append-only: the timeline never offers edit affordances. */
  events: TimelineEvent[];
  filter?: TimelineFilter;
  defaultFilter?: TimelineFilter;
  onFilterChange?: (filter: TimelineFilter) => void;
  /** Hide the type filter (e.g. inside a narrow card). */
  showFilter?: boolean;
  /** Extra filters next to the type filter (actor combobox, date range picker). */
  filterSlot?: ReactNode;
  /** More, older events exist: shows «Φόρτωση παλαιότερων». The set size is then unknown (−1). */
  hasMore?: boolean;
  onLoadOlder?: () => void;
  isLoadingOlder?: boolean;
  state?: 'ready' | 'loading' | 'error';
  onRetry?: () => void;
  errorMessage?: string;
  /** Fixed clock for tests and stories; defaults to a shared minute clock. */
  now?: Date;
  /** Level of the day headings (default 3, under the record's section heading). */
  headingLevel?: 2 | 3 | 4;
}

const filters: TimelineFilter[] = [
  'all',
  'transactions',
  'documents',
  'communication',
  'fields',
  'ai',
];

/**
 * Timeline (Part 2 §4.32): a labelled region with sticky day headings (real headings, D-FE-19) and a list
 * of events per day, same-actor edits collapsed, a type filter, «Φόρτωση παλαιότερων», MI-48 live insert.
 * The feed role is deliberately not used: a feed may not contain headings, and headings matter more for
 * screen-reader navigation.
 */
export function Timeline({
  'aria-label': ariaLabel,
  events,
  filter: controlledFilter,
  defaultFilter = 'all',
  onFilterChange,
  showFilter = true,
  filterSlot,
  hasMore = false,
  onLoadOlder,
  isLoadingOlder = false,
  state = 'ready',
  onRetry,
  errorMessage,
  now: fixedNow,
  headingLevel = 3,
}: TimelineProps) {
  const { t, i18n } = useTranslation('ds');
  const now = useNow(fixedNow);
  const [uncontrolledFilter, setUncontrolledFilter] = useState<TimelineFilter>(defaultFilter);
  const filter = controlledFilter ?? uncontrolledFilter;

  const visible = filterEvents(events, filter);
  const days = groupTimeline(visible);

  const filterLabel = (f: TimelineFilter): string => {
    switch (f) {
      case 'all':
        return t('timeline.filters.all');
      case 'transactions':
        return t('timeline.filters.transactions');
      case 'documents':
        return t('timeline.filters.documents');
      case 'communication':
        return t('timeline.filters.communication');
      case 'fields':
        return t('timeline.filters.fields');
      case 'ai':
        return t('timeline.filters.ai');
    }
  };

  // Announce live inserts politely, once per batch.
  const announced = useRef(new Set<string>());
  useEffect(() => {
    const fresh = events.filter((e) => e.isNew && !announced.current.has(e.id));
    if (fresh.length === 0) return;
    for (const e of fresh) announced.current.add(e.id);
    announce(t('timeline.newEvents', { count: fresh.length }));
  }, [events, t]);

  let body: ReactNode;
  if (state === 'loading') {
    body = (
      <div className={styles.loading} aria-busy="true">
        <span className="ds-visually-hidden">{t('timeline.loading')}</span>
        {[0, 1, 2].map((i) => (
          <div key={i} className={styles.skeletonItem}>
            <SkeletonBlock width="8px" height="8px" shape="circle" />
            <span className={styles.skeletonLines}>
              <SkeletonBlock width="70%" />
              <SkeletonBlock width="30%" />
            </span>
          </div>
        ))}
      </div>
    );
  } else if (state === 'error') {
    body = (
      <ErrorState
        {...(errorMessage ? { message: errorMessage } : {})}
        {...(onRetry ? { onRetry } : {})}
      />
    );
  } else if (visible.length === 0) {
    body = (
      <p className={styles.empty} role="status">
        {filter === 'all' ? t('timeline.emptyAll') : t('timeline.empty')}
      </p>
    );
  } else {
    const DayHeading = `h${String(headingLevel)}` as 'h2' | 'h3' | 'h4';
    body = (
      <section
        className={styles.feed}
        aria-label={ariaLabel}
        aria-busy={isLoadingOlder || undefined}
      >
        {days.map((day) => (
          <div key={day.key} className={styles.day}>
            {/* D-FE-19: real day headings (screen-reader navigation) and one list per day; no feed role. */}
            <DayHeading className={styles.dayHeader}>
              {dayLabel(day.date, now, i18n.language, {
                today: t('timeline.today'),
                yesterday: t('timeline.yesterday'),
              })}
            </DayHeading>
            <ul className={styles.dayList}>
              {day.entries.map((entry) => (
                <TimelineItem
                  key={entry.type === 'group' ? entry.id : entry.event.id}
                  entry={entry}
                  now={now}
                />
              ))}
            </ul>
          </div>
        ))}
      </section>
    );
  }

  return (
    <div className={styles.timeline}>
      {showFilter || filterSlot ? (
        <div className={styles.filters}>
          {showFilter ? (
            <RadioGroup
              className={cx(styles.segmented)}
              orientation="horizontal"
              value={filter}
              onChange={(value) => {
                const next = value as TimelineFilter;
                setUncontrolledFilter(next);
                onFilterChange?.(next);
              }}
            >
              <Label className="ds-visually-hidden">{t('timeline.filterLabel')}</Label>
              {filters.map((f) => (
                <RadioField key={f} value={f} className={cx(styles.segmentField)}>
                  <RadioButton className={cx(styles.segment)}>{filterLabel(f)}</RadioButton>
                </RadioField>
              ))}
            </RadioGroup>
          ) : null}
          {filterSlot}
        </div>
      ) : null}
      {body}
      {state === 'ready' && hasMore && onLoadOlder ? (
        <div className={styles.more}>
          <Button variant="secondary" size="sm" isLoading={isLoadingOlder} onPress={onLoadOlder}>
            {t('timeline.loadOlder')}
          </Button>
        </div>
      ) : null}
    </div>
  );
}
