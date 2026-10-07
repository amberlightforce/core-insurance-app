import { useState, type CSSProperties } from 'react';
import { Button as AriaButton, DialogTrigger, Popover as AriaPopover } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { cx } from '../../utils/cx';
import { useModalDepth } from '../Dialog/modalContext';
import { PopoverDialog } from '../Popover';
import styles from './Avatar.module.css';
import { avatarHue, initialsOf } from './avatarColor';

/** xs 20 (cells, mentions) · sm 24 (stacks, comments) · md 32 · lg 48 (party header) · xl 64. */
export type AvatarSize = 'xs' | 'sm' | 'md' | 'lg' | 'xl';
/** Presence ring: viewing `success.solid`, editing `brand.solid` + MI-46 pulse, idle neutral at 50 %. */
export type Presence = 'viewing' | 'editing' | 'idle';
export type AvatarStatusTone = 'success' | 'warning' | 'danger' | 'neutral';

export interface AvatarProps {
  name: string;
  /** Photo URL; falls back to initials if it fails to load. */
  src?: string;
  size?: AvatarSize;
  /** Organisations are `radius.md` squares. */
  kind?: 'person' | 'organisation';
  presence?: Presence;
  /** 8 px status dot with a 2 px sheet cutout; `statusLabel` is its accessible name. */
  status?: AvatarStatusTone;
  statusLabel?: string;
  /** The name is shown next to the avatar: hide it from assistive technology (`alt=""`). */
  decorative?: boolean;
  className?: string;
}

/**
 * Avatar (Part 2 §4.25): a photo or two initials (no tonos) on a deterministic `--color-avatar-1…8`
 * background, an optional presence ring and an optional status dot.
 */
export function Avatar({
  name,
  src,
  size = 'md',
  kind = 'person',
  presence,
  status,
  statusLabel,
  decorative = false,
  className,
}: AvatarProps) {
  const { t } = useTranslation('ds');
  const [failed, setFailed] = useState(false);
  const label = presence ? t('avatar.label', { name, presence }) : name;
  const showImage = src !== undefined && !failed;

  return (
    <span
      className={cx(styles.avatar, className)}
      data-size={size}
      data-kind={kind}
      data-presence={presence}
      style={{ '--_hue': `var(--color-avatar-${String(avatarHue(name))})` } as CSSProperties}
    >
      {showImage ? (
        <img
          className={cx(styles.face)}
          src={src}
          alt={decorative ? '' : label}
          onError={() => {
            setFailed(true);
          }}
        />
      ) : (
        <span
          className={cx(styles.face, styles.initials)}
          {...(decorative ? { 'aria-hidden': true } : { role: 'img', 'aria-label': label })}
        >
          <span aria-hidden="true">{initialsOf(name)}</span>
        </span>
      )}
      {presence === 'editing' ? <span className={cx(styles.pulse)} aria-hidden="true" /> : null}
      {status ? (
        <span
          className={cx(styles.status)}
          data-tone={status}
          {...(statusLabel ? { role: 'img', 'aria-label': statusLabel } : { 'aria-hidden': true })}
        />
      ) : null}
    </span>
  );
}

export interface PresencePerson {
  id: string;
  name: string;
  src?: string;
  presence: Presence;
  /** Section being viewed or edited, e.g. «Καλύψεις». */
  section?: string;
  /** Time they joined, already formatted («14:02»). */
  since: string;
}

export interface PresenceStackProps {
  people: PresencePerson[];
  /** Avatars shown before the «+N» button (default 4). */
  max?: number;
  className?: string;
}

/**
 * Presence stack (Part 2 §4.25): up to 4 overlapping 24 px avatars with presence rings, then a «+N»
 * button (RAC `Button` + `DialogTrigger`) that opens a popover listing everyone on the record
 * («Μαρία Παπαδοπούλου · επεξεργάζεται: Καλύψεις · από 14:02»).
 */
export function PresenceStack({ people, max = 4, className }: PresenceStackProps) {
  const { t } = useTranslation('ds');
  const inModal = useModalDepth() > 0;
  const shown = people.slice(0, max);
  const hidden = people.length - shown.length;

  return (
    <div className={cx(styles.stack, className)} role="group" aria-label={t('avatar.stackLabel')}>
      {shown.map((person) => (
        <Avatar
          key={person.id}
          name={person.name}
          size="sm"
          presence={person.presence}
          {...(person.src ? { src: person.src } : {})}
          className={cx(styles.stacked)}
        />
      ))}
      {hidden > 0 ? (
        <DialogTrigger>
          <AriaButton
            className={cx(styles.more)}
            aria-label={t('avatar.more', { count: hidden })}
            aria-haspopup="dialog"
          >
            {t('avatar.moreShort', { count: hidden })}
          </AriaButton>
          <AriaPopover
            className={cx(styles.listPopover)}
            placement="bottom end"
            offset={8}
            data-material="popover"
            data-solid={inModal || undefined}
          >
            <PopoverDialog title={t('avatar.listTitle')}>
              <ul className={cx(styles.list)}>
                {people.map((person) => (
                  <li key={person.id} className={cx(styles.row)}>
                    <Avatar
                      name={person.name}
                      size="xs"
                      presence={person.presence}
                      decorative
                      {...(person.src ? { src: person.src } : {})}
                    />
                    <span>
                      {person.section
                        ? t('avatar.rowSection', {
                            name: person.name,
                            presence: person.presence,
                            section: person.section,
                            since: person.since,
                          })
                        : t('avatar.row', {
                            name: person.name,
                            presence: person.presence,
                            since: person.since,
                          })}
                    </span>
                  </li>
                ))}
              </ul>
            </PopoverDialog>
          </AriaPopover>
        </DialogTrigger>
      ) : null}
    </div>
  );
}

export interface FieldLockProps {
  /** Short name, e.g. «Μ. Παπαδοπούλου». */
  name: string;
  /** Grammatical gender from the person record, for the Greek article (D.1); `other` avoids it. */
  gender?: 'female' | 'male' | 'other';
  src?: string;
}

/** Field-level lock line (Part 2 §4.25): xs avatar + «Επεξεργάζεται η Μ. Παπαδοπούλου», announced politely. */
export function FieldLock({ name, gender = 'other', src }: FieldLockProps) {
  const { t } = useTranslation('ds');
  return (
    <span className={cx(styles.lock)} role="status">
      <Avatar name={name} size="xs" presence="editing" decorative {...(src ? { src } : {})} />
      <span>{t('avatar.fieldLock', { name, gender })}</span>
    </span>
  );
}
