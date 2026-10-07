import { Search, X } from 'lucide-react';
import { useEffect, useRef, useState, type CSSProperties, type ReactNode } from 'react';
import {
  Autocomplete,
  Button as AriaButton,
  Dialog as AriaDialog,
  Header,
  Input,
  Menu as AriaMenu,
  MenuItem as AriaMenuItem,
  MenuSection as AriaMenuSection,
  Modal,
  ModalOverlay,
  SearchField,
  Text,
  type Key,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import type { MatchRange } from '../../../format/search';
import { toGreekUpper } from '../../../format/greek';
import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { ModalDepthContext, useModalDepth } from '../Dialog/modalContext';
import { Kbd, matchesShortcut } from '../Kbd';
import styles from './CommandPalette.module.css';
import {
  buildSections,
  nextScope,
  parsePrefix,
  type PaletteGroup,
  type PaletteHit,
  type PaletteItem,
  type PaletteScope,
} from './paletteQuery';

const FULL_TEXT_KEY = '__full-text';

export interface CommandPaletteProps {
  isOpen: boolean;
  onOpenChange: (isOpen: boolean) => void;
  /** Local actions, destinations, records and recents (`group` decides the section). */
  items: PaletteItem[];
  /** Remote record search (WRK palette back-end); called as the query changes. */
  onSearch?: (query: string, scope: PaletteScope | null) => Promise<PaletteItem[]>;
  /** Ctrl+Enter on a highlighted item. */
  onOpenInNewTab?: (item: PaletteItem) => void;
  /** «Αναζήτηση σε πλήρες κείμενο (↵)» when nothing matches. */
  onFullTextSearch?: (query: string) => void;
  /** Initial scope chip, e.g. `policy` when opened from the policies module. */
  defaultScope?: PaletteScope;
}

function Highlighted({ text, ranges }: { text: string; ranges: MatchRange[] }) {
  if (ranges.length === 0) return <>{text}</>;
  const parts: ReactNode[] = [];
  let cursor = 0;
  for (const range of ranges) {
    if (range.start > cursor) parts.push(text.slice(cursor, range.start));
    parts.push(
      <mark key={range.start} className={styles.mark}>
        {text.slice(range.start, range.end)}
      </mark>,
    );
    cursor = range.end;
  }
  if (cursor < text.length) parts.push(text.slice(cursor));
  return <>{parts}</>;
}

function groupTitleKey(group: PaletteGroup) {
  switch (group) {
    case 'actions':
      return 'commandPalette.groups.actions' as const;
    case 'navigation':
      return 'commandPalette.groups.navigation' as const;
    case 'records':
      return 'commandPalette.groups.records' as const;
    default:
      return 'commandPalette.groups.recent' as const;
  }
}

/**
 * Command palette (Part 2 §4.36, DESIGN-B A.9) on RAC `ModalOverlay`/`Modal`/`Dialog` + `Autocomplete` +
 * `SearchField` + `Menu` with sections: 640 px, `radius.2xl`, `elevation.5`, at 14vh. Prefixes
 * `ασφ:`/`pol:`, `ζημ:`/`clm:`, `πελ:`/`cus:`, `>` and `#` become a removable scope chip (Backspace on an
 * empty input); Tab cycles the type filter; matching is accent-insensitive and Greeklish-tolerant; Enter
 * runs, Ctrl+Enter opens in a new tab, Esc closes. Pair with `useCommandPaletteShortcut`.
 */
export function CommandPalette(props: CommandPaletteProps) {
  const { isOpen, onOpenChange } = props;
  const depth = useModalDepth();
  return (
    <ModalOverlay
      className={cx(styles.overlay)}
      isOpen={isOpen}
      onOpenChange={onOpenChange}
      isDismissable
    >
      <Modal className={cx(styles.palette)} data-material="overlay">
        <ModalDepthContext value={depth + 1}>
          <PaletteDialog {...props} />
        </ModalDepthContext>
      </Modal>
    </ModalOverlay>
  );
}

type SearchStatus = 'idle' | 'loading' | 'error';

function PaletteDialog({
  items,
  onSearch,
  onOpenInNewTab,
  onFullTextSearch,
  defaultScope,
  onOpenChange,
}: CommandPaletteProps) {
  const { t } = useTranslation('ds');
  const [query, setQuery] = useState('');
  const [scope, setScope] = useState<PaletteScope | null>(defaultScope ?? null);
  const [remote, setRemote] = useState<PaletteItem[]>([]);
  const [status, setStatus] = useState<SearchStatus>('idle');
  const requestId = useRef(0);
  const menuRef = useRef<HTMLDivElement | null>(null);

  const sections = buildSections(items, remote, query, scope);
  const hits = sections.flatMap((section) => section.hits);
  const showFullText = query.trim() !== '' && hits.length === 0 && status !== 'loading';
  const count = hits.length;

  // Polite result count (Part 2 §4.36).
  useEffect(() => {
    if (query.trim() === '' && scope === null) return;
    announce(t('commandPalette.count', { count }));
  }, [count, query, scope, t]);

  const search = (text: string, nextScopeValue: PaletteScope | null) => {
    if (!onSearch || text.trim() === '' || nextScopeValue === 'actions') {
      requestId.current += 1;
      setRemote([]);
      setStatus('idle');
      return;
    }
    requestId.current += 1;
    const id = requestId.current;
    setStatus('loading');
    onSearch(text, nextScopeValue).then(
      (results) => {
        if (id !== requestId.current) return;
        setRemote(results);
        setStatus('idle');
      },
      () => {
        if (id !== requestId.current) return;
        setRemote([]);
        setStatus('error');
      },
    );
  };

  const changeQuery = (value: string) => {
    let text = value;
    let nextScopeValue = scope;
    if (scope === null) {
      const parsed = parsePrefix(value);
      if (parsed.scope) {
        nextScopeValue = parsed.scope;
        text = parsed.rest;
        setScope(parsed.scope);
      }
    }
    setQuery(text);
    search(text, nextScopeValue);
  };

  const changeScope = (next: PaletteScope | null) => {
    setScope(next);
    search(query, next);
  };

  const close = () => {
    onOpenChange(false);
  };

  const find = (key: Key): PaletteItem | undefined => hits.find((hit) => hit.item.id === key)?.item;

  const run = (key: Key) => {
    if (key === FULL_TEXT_KEY) {
      onFullTextSearch?.(query);
      close();
      return;
    }
    const item = find(key);
    if (!item) return;
    close();
    item.onAction();
  };

  return (
    <AriaDialog className={cx(styles.dialog)} aria-label={t('commandPalette.label')}>
      <Autocomplete inputValue={query} onInputChange={changeQuery}>
        <div className={styles.inputRow}>
          <span className={styles.searchIcon} aria-hidden="true">
            <Icon icon={Search} size={20} />
          </span>
          {scope ? (
            <AriaButton
              className={cx(styles.scope)}
              excludeFromTabOrder
              aria-label={t('commandPalette.removeScope')}
              onPress={() => {
                changeScope(null);
              }}
            >
              <span>{t('commandPalette.scope', { scope })}</span>
              <Icon icon={X} size={14} />
            </AriaButton>
          ) : null}
          <SearchField
            className={cx(styles.field)}
            aria-label={t('commandPalette.label')}
            autoFocus
          >
            <Input
              className={cx(styles.input)}
              placeholder={t('commandPalette.placeholder')}
              onKeyDown={(event) => {
                if (event.key === 'Escape') {
                  event.preventDefault();
                  close();
                } else if (event.key === 'Backspace' && query === '' && scope !== null) {
                  event.preventDefault();
                  changeScope(null);
                } else if (event.key === 'Tab' && !event.shiftKey) {
                  event.preventDefault();
                  changeScope(nextScope(scope));
                } else if (matchesShortcut(event, 'Mod+Enter')) {
                  event.preventDefault();
                  const focused = menuRef.current?.querySelector<HTMLElement>('[data-focused]');
                  const item = focused?.dataset.key ? find(focused.dataset.key) : undefined;
                  if (item && onOpenInNewTab) {
                    onOpenInNewTab(item);
                    close();
                  }
                }
              }}
            />
          </SearchField>
          <Kbd shortcut="Esc" />
        </div>
        {status === 'error' ? (
          <p className={styles.notice} role="status">
            {t('commandPalette.error')}
          </p>
        ) : null}
        <AriaMenu
          ref={menuRef}
          className={cx(styles.list)}
          aria-label={t('commandPalette.label')}
          data-stale={status === 'loading' || undefined}
          onAction={run}
        >
          {sections.map((section) => (
            <AriaMenuSection key={section.group} id={section.group} className={cx(styles.section)}>
              <Header className={cx(styles.header)}>
                {toGreekUpper(t(groupTitleKey(section.group)))}
              </Header>
              {section.hits.map((hit, index) => (
                <PaletteRow
                  key={hit.item.id}
                  hit={hit}
                  index={index}
                  openLabel={t('commandPalette.open')}
                />
              ))}
            </AriaMenuSection>
          ))}
          {showFullText ? (
            <AriaMenuSection id="full-text" className={cx(styles.section)}>
              <Header className={cx(styles.header)}>
                {toGreekUpper(t('commandPalette.groups.fullText'))}
              </Header>
              <AriaMenuItem id={FULL_TEXT_KEY} className={cx(styles.item)} textValue={query}>
                <span className={styles.itemIcon} aria-hidden="true">
                  <Icon icon={Search} size={16} />
                </span>
                <span className={styles.itemText}>
                  <Text slot="label" className={cx(styles.itemTitle)}>
                    {t('commandPalette.noResults')}
                  </Text>
                </span>
              </AriaMenuItem>
            </AriaMenuSection>
          ) : null}
        </AriaMenu>
      </Autocomplete>
      <div className={styles.footer} aria-label={t('commandPalette.hints.label')} role="group">
        <span className={styles.hint}>
          <Kbd shortcut="↑" />
          <Kbd shortcut="↓" />
          {t('commandPalette.hints.navigate')}
        </span>
        <span className={styles.hint}>
          <Kbd shortcut="Enter" />
          {t('commandPalette.hints.open')}
        </span>
        <span className={styles.hint}>
          <Kbd shortcut="Mod+Enter" />
          {t('commandPalette.hints.newTab')}
        </span>
        <span className={styles.hint}>
          <Kbd shortcut="Tab" />
          {t('commandPalette.hints.filter')}
        </span>
        <span className={styles.hint}>
          <Kbd shortcut="Esc" />
          {t('commandPalette.hints.close')}
        </span>
      </div>
    </AriaDialog>
  );
}

function PaletteRow({
  hit,
  index,
  openLabel,
}: {
  hit: PaletteHit;
  index: number;
  openLabel: string;
}) {
  const { item } = hit;
  const style = { '--_i': String(Math.min(index, 8)) } as CSSProperties;
  return (
    <AriaMenuItem id={item.id} className={cx(styles.item)} textValue={item.title} style={style}>
      {({ isFocused }) => (
        <>
          <span className={styles.itemIcon} aria-hidden="true">
            {item.icon ? <Icon icon={item.icon} size={16} /> : null}
          </span>
          <span className={styles.itemText}>
            <Text slot="label" className={cx(styles.itemTitle)}>
              <Highlighted text={item.title} ranges={hit.ranges} />
            </Text>
            {item.subtitle ? (
              <Text slot="description" className={cx(styles.itemSubtitle)}>
                {item.subtitle}
              </Text>
            ) : null}
          </span>
          {item.shortcut ? (
            <Kbd shortcut={item.shortcut} />
          ) : isFocused ? (
            <span className={styles.openHint} aria-hidden="true">
              <Kbd shortcut="Enter" tone="subtle" />
              {openLabel}
            </span>
          ) : null}
        </>
      )}
    </AriaMenuItem>
  );
}
