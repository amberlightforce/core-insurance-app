import { ArrowRight, ChevronRight, Cpu, Sparkles } from 'lucide-react';
import { useId, type CSSProperties } from 'react';
import {
  Button as AriaButton,
  Disclosure,
  DisclosurePanel,
  Focusable,
  Link,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { useReducedMotion, useRegionFormat } from '../../preferences/context';
import { cx, defined } from '../../utils/cx';
import { Tooltip } from '../Tooltip';
import styles from './Timeline.module.css';
import {
  absoluteTime,
  avatarIndex,
  initialsOf,
  relativeTime,
  type TimelineActor,
  type TimelineDiff,
  type TimelineEntry,
  type TimelineEvent,
} from './timelineModel';

function Actor({ actor }: { actor: TimelineActor }) {
  const { t } = useTranslation('ds');
  if (actor.kind === 'system') {
    return (
      <span className={styles.actor}>
        <span className={styles.actorIcon}>
          <Icon icon={Cpu} size={12} />
        </span>
        <span className={styles.actorName}>{t('timeline.system')}</span>
      </span>
    );
  }
  if (actor.kind === 'agent') {
    return (
      <span className={styles.actor} data-ai="true">
        <span className={styles.actorIcon}>
          <Icon icon={Sparkles} size={12} />
        </span>
        <span className={styles.actorName}>
          {t('timeline.agentFor', { name: actor.onBehalfOf })}
        </span>
      </span>
    );
  }
  return (
    <span className={styles.actor}>
      <span
        className={styles.avatar}
        style={
          { '--_avatar': `var(--color-avatar-${String(avatarIndex(actor.id))})` } as CSSProperties
        }
        aria-hidden="true"
      >
        {actor.initials ?? initialsOf(actor.name)}
      </span>
      <span className={styles.actorName}>{actor.name}</span>
    </span>
  );
}

function Diff({ diff }: { diff: TimelineDiff }) {
  const { t } = useTranslation('ds');
  return (
    <p className={styles.diff}>
      <span aria-hidden="true">
        <span className={styles.diffField}>{diff.field}:</span>{' '}
        <del className={styles.diffFrom}>{diff.from}</del> <Icon icon={ArrowRight} size={12} />{' '}
        <ins className={styles.diffTo}>{diff.to}</ins>
      </span>
      <span className="ds-visually-hidden">
        {t('timeline.diff', { field: diff.field, from: diff.from, to: diff.to })}
      </span>
    </p>
  );
}

function Time({ at, now }: { at: Date; now: number }) {
  const { i18n } = useTranslation('ds');
  const region = useRegionFormat();
  const absolute = absoluteTime(at, region);
  return (
    <span className={styles.time}>
      <Tooltip content={absolute}>
        <Focusable>
          <time className={styles.relative} dateTime={at.toISOString()} tabIndex={-1}>
            {relativeTime(at, now, i18n.language, region)}
          </time>
        </Focusable>
      </Tooltip>
      <span className={styles.absolute}>{absolute}</span>
    </span>
  );
}

function Headline({ event, id }: { event: TimelineEvent; id: string }) {
  return (
    <p className={styles.headline} id={id}>
      <Actor actor={event.actor} /> <span className={styles.action}>{event.action}</span>
      {event.object ? (
        <>
          {' '}
          {event.object.href !== undefined || event.object.onPress !== undefined ? (
            <Link
              className={cx(styles.objectLink)}
              {...defined({ href: event.object.href, onPress: event.object.onPress })}
            >
              {event.object.label}
            </Link>
          ) : (
            <span className={styles.object}>{event.object.label}</span>
          )}
        </>
      ) : null}
    </p>
  );
}

function Node({ event }: { event: TimelineEvent }) {
  if (event.icon) {
    return (
      <span className={styles.iconNode} data-family={event.family ?? 'brand'} aria-hidden="true">
        <Icon icon={event.icon} size={12} />
      </span>
    );
  }
  return <span className={styles.dot} data-family={event.family ?? 'neutral'} aria-hidden="true" />;
}

interface TimelineItemProps {
  entry: TimelineEntry;
  now: number;
}

/**
 * One list item (D-FE-19: a list per day under a real day heading, no feed role): a single event, or a
 * collapsed run of field edits by the same person.
 */
export function TimelineItem({ entry, now }: TimelineItemProps) {
  const { t } = useTranslation('ds');
  const reduced = useReducedMotion();
  const headlineId = useId();

  if (entry.type === 'group') {
    const latest = entry.events[0];
    if (!latest) return null;
    const count = entry.events.reduce((sum, e) => sum + Math.max(1, e.diffs?.length ?? 0), 0);
    return (
      <li className={styles.article} data-timeline-item="">
        <Node event={latest} />
        <div className={styles.card}>
          <p className={styles.headline} id={headlineId}>
            <Actor actor={entry.actor} />{' '}
            <span className={styles.action}>{t('timeline.groupedChanges', { count })}</span>
          </p>
          <Time at={latest.at} now={now} />
          <Disclosure className={cx(styles.disclosure)}>
            <AriaButton slot="trigger" className={cx(styles.expand)}>
              <span className={styles.chevron}>
                <Icon icon={ChevronRight} size={12} />
              </span>
              {t('timeline.showChanges')}
            </AriaButton>
            <DisclosurePanel>
              <ul className={styles.groupList}>
                {entry.events.map((e) => (
                  <li key={e.id}>
                    {e.diffs && e.diffs.length > 0 ? (
                      e.diffs.map((d) => <Diff key={`${e.id}-${d.field}`} diff={d} />)
                    ) : (
                      <span>{e.action}</span>
                    )}
                  </li>
                ))}
              </ul>
            </DisclosurePanel>
          </Disclosure>
        </div>
      </li>
    );
  }

  const { event } = entry;
  return (
    <li
      className={styles.article}
      data-timeline-item=""
      data-key={event.icon ? 'true' : undefined}
      data-new={event.isNew === true ? true : undefined}
    >
      <Node event={event} />
      <div className={styles.card}>
        <Headline event={event} id={headlineId} />
        <span className={styles.metaRow}>
          <Time at={event.at} now={now} />
          {event.isNew && reduced ? (
            <span className={styles.newBadge}>{t('timeline.new')}</span>
          ) : null}
        </span>
        {event.diffs?.map((d) => (
          <Diff key={d.field} diff={d} />
        ))}
        {event.trace ? (
          event.trace.href ? (
            <Link className={cx(styles.trace)} href={event.trace.href}>
              {event.trace.label}
            </Link>
          ) : (
            <span className={styles.trace}>{event.trace.label}</span>
          )
        ) : null}
      </div>
    </li>
  );
}
