import { Keyboard, Search } from 'lucide-react';
import { useEffect, useId, useState } from 'react';
import { Input, SearchField } from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { matchesQuery } from '../../../format/search';
import { Icon } from '../../icons';
import { usePreferences } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { Dialog } from '../Dialog';
import { Kbd } from '../Kbd';
import { isTextEntry } from './isTextEntry';
import styles from './ShortcutOverlay.module.css';

export interface ShortcutEntry {
  /** Keys joined with `+` (`Mod+K`, `Shift+F10`) or a sequence written with spaces (`G P`). */
  keys: string;
  description: string;
}

export interface ShortcutGroup {
  /** Context, e.g. «Γενικά», «Ουρά εργασιών», «Πίνακας». */
  title: string;
  shortcuts: ShortcutEntry[];
}

export interface ShortcutOverlayProps {
  groups: ShortcutGroup[];
  /** Controlled open state; omit to let `?` open it. */
  isOpen?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
}

function ShortcutList({ groups, query }: { groups: ShortcutGroup[]; query: string }) {
  const { t } = useTranslation('ds');
  const idBase = useId();
  const filtered = groups
    .map((group) => ({
      ...group,
      shortcuts: group.shortcuts.filter(
        (entry) =>
          matchesQuery(entry.description, query) !== null ||
          matchesQuery(entry.keys, query) !== null,
      ),
    }))
    .filter((group) => group.shortcuts.length > 0);

  if (filtered.length === 0) {
    return (
      <p className={styles.empty} role="status">
        {t('shortcutOverlay.empty', { query })}
      </p>
    );
  }
  return (
    <div className={styles.groups}>
      {filtered.map((group, index) => {
        const headingId = `${idBase}-${String(index)}`;
        return (
          <section key={group.title} className={styles.group} aria-labelledby={headingId}>
            <h3 id={headingId} className={styles.groupTitle}>
              {group.title}
            </h3>
            <ul className={styles.rows}>
              {group.shortcuts.map((entry) => (
                <li key={`${entry.keys}-${entry.description}`} className={styles.row}>
                  <span>{entry.description}</span>
                  <Kbd shortcut={entry.keys} />
                </li>
              ))}
            </ul>
          </section>
        );
      })}
    </div>
  );
}

/**
 * Keyboard shortcut overlay (Part 2 §4.34): `?` outside text fields opens a searchable cheat sheet grouped
 * by context. The `?` key respects the «single-key shortcuts» preference (WCAG 2.1.4).
 */
export function ShortcutOverlay({ groups, isOpen, onOpenChange }: ShortcutOverlayProps) {
  const { t } = useTranslation('ds');
  const { preferences } = usePreferences();
  const [localOpen, setLocalOpen] = useState(false);
  const [query, setQuery] = useState('');
  const open = isOpen ?? localOpen;

  const setOpen = (next: boolean) => {
    if (!next) setQuery('');
    setLocalOpen(next);
    onOpenChange?.(next);
  };

  useEffect(() => {
    if (!preferences.singleKeyShortcuts) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== '?' || event.ctrlKey || event.metaKey || event.altKey) return;
      if (event.defaultPrevented || isTextEntry(event.target)) return;
      event.preventDefault();
      setLocalOpen(true);
      onOpenChange?.(true);
    };
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [preferences.singleKeyShortcuts, onOpenChange]);

  return (
    <Dialog
      title={t('shortcutOverlay.title')}
      icon={Keyboard}
      size="lg"
      hideCancel
      isOpen={open}
      onOpenChange={setOpen}
    >
      <SearchField
        className={cx(styles.search)}
        aria-label={t('shortcutOverlay.search')}
        value={query}
        onChange={setQuery}
      >
        <Icon icon={Search} size={16} />
        <Input className={cx(styles.input)} placeholder={t('shortcutOverlay.search')} />
      </SearchField>
      <ShortcutList groups={groups} query={query} />
    </Dialog>
  );
}
