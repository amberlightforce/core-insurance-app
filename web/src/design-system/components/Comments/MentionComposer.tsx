import {
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
} from 'react';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { cx } from '../../utils/cx';
import styles from './Comments.module.css';
import {
  activeQuery,
  emptyMentionValue,
  filterPeople,
  insertMention,
  rebaseMentions,
  type MentionPerson,
  type MentionValue,
} from './mentionModel';
import { avatarIndex, initialsOf } from '../Timeline/timelineModel';

export interface MentionComposerProps {
  /** Accessible label, e.g. «Νέο σχόλιο». */
  label: string;
  value?: MentionValue;
  defaultValue?: MentionValue;
  onChange?: (value: MentionValue) => void;
  /** People who can be mentioned. Filtering is accent- and case-insensitive. */
  people: MentionPerson[];
  /** Ctrl/⌘+Enter. */
  onSubmit?: (value: MentionValue) => void;
  placeholder?: string;
  isDisabled?: boolean;
  /** Grows with its content up to this many rows, then scrolls. */
  maxRows?: number;
  autoFocus?: boolean;
  /** Extra description ids (e.g. a send-failure message). */
  'aria-describedby'?: string;
}

/**
 * @mention composer (D-FE-08, Part 2 §4.33): an auto-growing textarea with list autocomplete. Typing «@»
 * opens a listbox of people; ↑/↓ move, Enter or Tab insert, Esc closes. Inserted mentions are tokens in the
 * value model `{ text, mentions: [{ id, name, start, end }] }`. Ctrl/⌘+Enter submits.
 *
 * ARIA (D-FE-20, see README.md): a multi-line `<textarea>` may not take `role="combobox"` or
 * `aria-expanded`, and we do not use `aria-activedescendant`. Keyboard is handled on the textarea; the
 * textarea's `aria-describedby` points at the instructions; the suggestion count and the highlighted
 * suggestion are announced through the live announcer. The popup is a listbox for pointer users.
 */
export function MentionComposer({
  label,
  value: controlled,
  defaultValue = emptyMentionValue,
  onChange,
  people,
  onSubmit,
  placeholder,
  isDisabled = false,
  maxRows = 8,
  autoFocus = false,
  'aria-describedby': describedBy,
}: MentionComposerProps) {
  const { t } = useTranslation('ds');
  const [uncontrolled, setUncontrolled] = useState(defaultValue);
  const value = controlled ?? uncontrolled;
  const [query, setQuery] = useState<{ start: number; query: string } | null>(null);
  const [active, setActive] = useState(0);
  const textarea = useRef<HTMLTextAreaElement>(null);
  const pendingCaret = useRef<number | null>(null);
  const listId = useId();
  const hintId = useId();

  const matches = query ? filterPeople(people, query.query) : [];
  const open = query !== null && matches.length > 0;
  const activeIndex = Math.min(active, Math.max(0, matches.length - 1));
  const activePerson = open ? matches[activeIndex] : undefined;

  const update = (next: MentionValue) => {
    if (controlled === undefined) setUncontrolled(next);
    onChange?.(next);
  };

  // Restore the caret after a programmatic insert.
  useLayoutEffect(() => {
    const el = textarea.current;
    if (el && pendingCaret.current !== null) {
      el.setSelectionRange(pendingCaret.current, pendingCaret.current);
      pendingCaret.current = null;
    }
  });

  // D-FE-20: no aria-activedescendant. The suggestion count is announced when the list opens or changes,
  // and the highlighted suggestion is announced as the user moves with ↑/↓.
  const suggestionCount = open ? matches.length : 0;
  useEffect(() => {
    if (suggestionCount > 0) announce(t('mentionComposer.count', { count: suggestionCount }));
  }, [suggestionCount, t]);
  const announcedActive = useRef<string | null>(null);
  useEffect(() => {
    if (!open) {
      announcedActive.current = null;
      return;
    }
    if (!activePerson || announcedActive.current === null) {
      // The first suggestion is covered by the count announcement.
      announcedActive.current = activePerson?.id ?? null;
      return;
    }
    if (announcedActive.current === activePerson.id) return;
    announcedActive.current = activePerson.id;
    announce(
      t('mentionComposer.current', {
        name: activePerson.name,
        position: activeIndex + 1,
        count: matches.length,
      }),
    );
  }, [open, activePerson, activeIndex, matches.length, t]);

  // Auto-grow: the height follows the content up to maxRows.
  useEffect(() => {
    const el = textarea.current;
    if (!el) return;
    el.style.blockSize = 'auto';
    const lineHeight = Number.parseFloat(getComputedStyle(el).lineHeight) || 20;
    const max = lineHeight * maxRows;
    el.style.blockSize = `${String(Math.min(el.scrollHeight, max))}px`;
    el.style.overflowY = el.scrollHeight > max ? 'auto' : 'hidden';
  }, [value.text, maxRows]);

  const refreshQuery = (text: string, caret: number) => {
    const next = activeQuery(text, caret);
    setQuery(next);
    setActive(0);
  };

  const choose = (person: MentionPerson) => {
    if (!query) return;
    const caret = textarea.current?.selectionStart ?? value.text.length;
    const result = insertMention(value, person, query.start, caret);
    pendingCaret.current = result.caret;
    update(result.value);
    setQuery(null);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      setQuery(null);
      onSubmit?.(value);
      return;
    }
    if (!open) return;
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      setActive((activeIndex + 1) % matches.length);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      setActive((activeIndex - 1 + matches.length) % matches.length);
    } else if ((event.key === 'Enter' || event.key === 'Tab') && activePerson) {
      event.preventDefault();
      choose(activePerson);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      setQuery(null);
    }
  };

  const optionId = (id: string) => `${listId}-${id}`;

  return (
    <div className={styles.composer}>
      <textarea
        ref={textarea}
        className={styles.textarea}
        rows={1}
        value={value.text}
        placeholder={placeholder}
        disabled={isDisabled}
        autoFocus={autoFocus}
        aria-label={label}
        aria-autocomplete="list"
        aria-describedby={[hintId, describedBy].filter(Boolean).join(' ')}
        onChange={(event) => {
          const text = event.target.value;
          update({ text, mentions: rebaseMentions(value, text) });
          refreshQuery(text, event.target.selectionStart);
        }}
        onSelect={(event) => {
          const el = event.currentTarget;
          if (el.selectionStart === el.selectionEnd) refreshQuery(el.value, el.selectionStart);
        }}
        onKeyDown={onKeyDown}
        onBlur={() => {
          setQuery(null);
        }}
      />
      <span id={hintId} className="ds-visually-hidden">
        {t('mentionComposer.hint')}
      </span>
      {open ? (
        <ul
          id={listId}
          role="listbox"
          className={styles.mentionList}
          aria-label={t('mentionComposer.people')}
        >
          {matches.map((person, index) => (
            <li
              key={person.id}
              id={optionId(person.id)}
              role="option"
              aria-selected={index === activeIndex}
              className={cx(styles.mentionOption)}
              onMouseDown={(event) => {
                event.preventDefault();
                choose(person);
              }}
            >
              <span
                className={styles.avatar}
                style={
                  {
                    '--_avatar': `var(--color-avatar-${String(avatarIndex(person.id))})`,
                  } as CSSProperties
                }
                aria-hidden="true"
              >
                {person.initials ?? initialsOf(person.name)}
              </span>
              <span className={styles.optionText}>
                <span>{person.name}</span>
                {person.meta ? <span className={styles.optionMeta}>{person.meta}</span> : null}
              </span>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
