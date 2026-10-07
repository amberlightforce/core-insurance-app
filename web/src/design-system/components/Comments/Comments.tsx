import { Check, MessageSquare, Pencil, RotateCcw, Send, Trash2 } from 'lucide-react';
import { useRef, useState, type CSSProperties } from 'react';
import {
  Button as AriaButton,
  Dialog,
  DialogTrigger,
  Heading,
  Popover,
  ToggleButton,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';
import { Icon } from '../../icons';
import { useRegionFormat } from '../../preferences/context';
import { cx } from '../../utils/cx';
import { Button } from '../Button';
import { absoluteTime, avatarIndex, initialsOf, relativeTime } from '../Timeline/timelineModel';
import { useNow } from '../Timeline/useNow';
import { CommentBody } from './CommentBody';
import styles from './Comments.module.css';
import { MentionComposer } from './MentionComposer';
import { emptyMentionValue, type MentionPerson, type MentionValue } from './mentionModel';

export interface CommentAuthor {
  id: string;
  name: string;
  initials?: string;
}

export interface CommentItem {
  id: string;
  author: CommentAuthor;
  at: Date;
  body: MentionValue;
  editedAt?: Date;
}

export interface CommentThreadProps {
  /** The field the thread is pinned to: «Ποσοστό απαλλαγής». */
  field: string;
  comments: CommentItem[];
  isResolved?: boolean;
  hasUnread?: boolean;
  /** Audit roles: the composer, edit and delete are hidden. */
  isReadOnly?: boolean;
  /** Resolved threads are hidden unless this is set («Εμφάνιση επιλυμένων»). */
  showResolved?: boolean;
  currentUserId: string;
  people: MentionPerson[];
  /** Own comments can be edited or deleted within this window (WRK setting); 15 minutes by default. */
  editWindowMs?: number;
  /** Persists a new comment. Reject to show «Δεν στάλθηκε · Επανάληψη». */
  onSend: (body: MentionValue) => Promise<void>;
  onEdit?: (id: string, body: MentionValue) => Promise<void>;
  onDelete?: (id: string) => void;
  onResolve?: () => void;
  onReopen?: () => void;
  mentionHref?: (personId: string) => string;
  /** Fixed clock for tests and stories. */
  now?: Date;
  defaultOpen?: boolean;
}

interface Pending {
  id: string;
  body: MentionValue;
  status: 'sending' | 'failed';
}

function Avatar({ author }: { author: CommentAuthor }) {
  return (
    <span
      className={styles.avatar}
      style={
        { '--_avatar': `var(--color-avatar-${String(avatarIndex(author.id))})` } as CSSProperties
      }
      aria-hidden="true"
    >
      {author.initials ?? initialsOf(author.name)}
    </span>
  );
}

/**
 * Pinned comment thread (Part 2 §4.33): a 16 px speech-bubble pin opening a 360 px non-modal popover with the
 * comments, resolve, and an @mention composer. Sending is optimistic (60 % opacity, MI-41).
 */
export function CommentThread({
  field,
  comments,
  isResolved = false,
  hasUnread = false,
  isReadOnly = false,
  showResolved = false,
  currentUserId,
  people,
  editWindowMs = 15 * 60 * 1000,
  onSend,
  onEdit,
  onDelete,
  onResolve,
  onReopen,
  mentionHref,
  now: fixedNow,
  defaultOpen = false,
}: CommentThreadProps) {
  const { t, i18n } = useTranslation('ds');
  const region = useRegionFormat();
  const now = useNow(fixedNow);
  const [draft, setDraft] = useState<MentionValue>(emptyMentionValue);
  const [pending, setPending] = useState<Pending[]>([]);
  const sequence = useRef(0);
  const [editing, setEditing] = useState<{ id: string; body: MentionValue } | null>(null);

  if (isResolved && !showResolved) return null;

  const count = comments.length;

  const send = async (body: MentionValue, retryId?: string) => {
    if (body.text.trim() === '') return;
    sequence.current += 1;
    const id = retryId ?? `pending-${String(sequence.current)}`;
    setPending((list) =>
      retryId
        ? list.map((p) => (p.id === retryId ? { ...p, status: 'sending' } : p))
        : [...list, { id, body, status: 'sending' }],
    );
    if (!retryId) setDraft(emptyMentionValue);
    try {
      await onSend(body);
      setPending((list) => list.filter((p) => p.id !== id));
      announce(t('comments.sent'));
    } catch {
      setPending((list) => list.map((p) => (p.id === id ? { ...p, status: 'failed' } : p)));
      announce(t('comments.notSent'), 'assertive');
    }
  };

  const saveEdit = async () => {
    if (!editing || !onEdit) return;
    await onEdit(editing.id, editing.body);
    setEditing(null);
  };

  return (
    <DialogTrigger {...(defaultOpen ? { defaultOpen } : {})}>
      <AriaButton
        className={cx(styles.pin)}
        data-resolved={isResolved || undefined}
        aria-label={t('comments.pinLabel', { field, count })}
      >
        <Icon icon={MessageSquare} size={16} />
        {count > 1 ? <span className={styles.pinCount}>{count}</span> : null}
        {hasUnread ? <span className={styles.unread} aria-hidden="true" /> : null}
        {hasUnread ? <span className="ds-visually-hidden">{t('comments.unread')}</span> : null}
      </AriaButton>
      <Popover
        className={cx(styles.popover)}
        placement="bottom end"
        isNonModal
        data-material="popover"
      >
        <Dialog className={cx(styles.thread)} aria-label={t('comments.threadLabel', { field })}>
          <header className={styles.header}>
            <Heading slot="title" className={cx(styles.field)}>
              {field}
            </Heading>
            {isReadOnly ? null : isResolved ? (
              onReopen ? (
                <Button variant="ghost" size="sm" icon={RotateCcw} onPress={onReopen}>
                  {t('comments.reopen')}
                </Button>
              ) : null
            ) : onResolve ? (
              <Button variant="ghost" size="sm" icon={Check} onPress={onResolve}>
                {t('comments.resolve')}
              </Button>
            ) : null}
          </header>
          {isResolved ? <p className={styles.resolvedNote}>{t('comments.resolved')}</p> : null}
          <ul className={styles.comments} aria-label={t('comments.listLabel')}>
            {comments.map((comment) => {
              const own = comment.author.id === currentUserId;
              const withinWindow = now - comment.at.getTime() <= editWindowMs;
              const canChange = own && withinWindow && !isReadOnly;
              return (
                <li key={comment.id} className={styles.comment}>
                  <Avatar author={comment.author} />
                  <div className={styles.commentMain}>
                    <div className={styles.commentHead}>
                      <span className={styles.author}>{comment.author.name}</span>
                      <time
                        className={styles.time}
                        dateTime={comment.at.toISOString()}
                        title={absoluteTime(comment.at, region)}
                      >
                        {relativeTime(comment.at, now, i18n.language, region)}
                      </time>
                      {comment.editedAt ? (
                        <span className={styles.time}>{t('comments.edited')}</span>
                      ) : null}
                    </div>
                    {editing?.id === comment.id ? (
                      <div className={styles.editBox}>
                        <MentionComposer
                          label={t('comments.editLabel')}
                          people={people}
                          value={editing.body}
                          onChange={(body) => {
                            setEditing({ id: comment.id, body });
                          }}
                          onSubmit={() => {
                            void saveEdit();
                          }}
                          autoFocus
                        />
                        <div className={styles.composerActions}>
                          <Button
                            variant="ghost"
                            size="sm"
                            onPress={() => {
                              setEditing(null);
                            }}
                          >
                            {t('comments.cancel')}
                          </Button>
                          <Button
                            variant="primary"
                            size="sm"
                            onPress={() => {
                              void saveEdit();
                            }}
                          >
                            {t('comments.save')}
                          </Button>
                        </div>
                      </div>
                    ) : (
                      <CommentBody body={comment.body} {...(mentionHref ? { mentionHref } : {})} />
                    )}
                    {canChange && editing?.id !== comment.id ? (
                      <div className={styles.commentActions}>
                        {onEdit ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            icon={Pencil}
                            label={t('comments.edit')}
                            onPress={() => {
                              setEditing({ id: comment.id, body: comment.body });
                            }}
                          />
                        ) : null}
                        {onDelete ? (
                          <Button
                            variant="ghost"
                            size="sm"
                            icon={Trash2}
                            label={t('comments.delete')}
                            onPress={() => {
                              onDelete(comment.id);
                            }}
                          />
                        ) : null}
                      </div>
                    ) : null}
                  </div>
                </li>
              );
            })}
            {pending.map((p) => (
              <li key={p.id} className={styles.comment} data-pending={p.status}>
                <Avatar
                  author={{
                    id: currentUserId,
                    name: people.find((x) => x.id === currentUserId)?.name ?? '',
                  }}
                />
                <div className={styles.commentMain}>
                  <CommentBody body={p.body} />
                  {p.status === 'sending' ? (
                    <span className={styles.time}>{t('comments.sending')}</span>
                  ) : (
                    <span className={styles.failed} role="alert">
                      {t('comments.notSent')}
                      <Button
                        variant="link"
                        size="sm"
                        onPress={() => {
                          void send(p.body, p.id);
                        }}
                      >
                        {t('comments.retry')}
                      </Button>
                    </span>
                  )}
                </div>
              </li>
            ))}
          </ul>
          {isReadOnly ? null : (
            <div className={styles.composerBox}>
              <MentionComposer
                label={t('comments.newComment')}
                placeholder={t('comments.placeholder')}
                people={people}
                value={draft}
                onChange={setDraft}
                onSubmit={(body) => {
                  void send(body);
                }}
              />
              <div className={styles.composerActions}>
                <Button
                  variant="primary"
                  size="sm"
                  icon={Send}
                  shortcut="Mod+Enter"
                  onPress={() => {
                    void send(draft);
                  }}
                >
                  {t('comments.send')}
                </Button>
              </div>
            </div>
          )}
        </Dialog>
      </Popover>
    </DialogTrigger>
  );
}

export interface ResolvedCommentsToggleProps {
  isSelected: boolean;
  onChange: (isSelected: boolean) => void;
  count?: number;
}

/** «Εμφάνιση επιλυμένων»: shows resolved threads, which are hidden by default. */
export function ResolvedCommentsToggle({
  isSelected,
  onChange,
  count,
}: ResolvedCommentsToggleProps) {
  const { t } = useTranslation('ds');
  return (
    <ToggleButton className={cx(styles.toggle)} isSelected={isSelected} onChange={onChange}>
      {count === undefined
        ? t('comments.showResolved')
        : t('comments.showResolvedCount', { count })}
    </ToggleButton>
  );
}
