import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

import { useIdempotencyKey } from '../../api/idempotency';
import { Button, EmptyState, Select, StatusPill, TextField, announce } from '../../design-system';
import { PortalHero, PortalPage, PortalProblem, PortalSection } from '../ui/PortalUi';
import { QueryView } from '../ui/QueryView';
import { usePortalFormat, usePortalTitle } from '../ui/hooks';
import rows from '../ui/pages.module.css';
import styles from '../ui/portal.module.css';
import {
  listMessages,
  portalKeys,
  sendMessage,
  type MessageThread,
  type MessageTopic,
} from './portalApi';

const topics: MessageTopic[] = ['GENERAL', 'POLICY', 'BILLING', 'CLAIM', 'DOCUMENT'];
const maxLength = 2000;

/** The compose box: a new thread (with a topic) or a reply on `threadId`. */
function Compose({ threadId }: { threadId?: string }) {
  const { t } = useTranslation('portal');
  const client = useQueryClient();
  const { keyFor, release } = useIdempotencyKey();
  const [text, setText] = useState('');
  const [topic, setTopic] = useState<MessageTopic>('GENERAL');
  const [tried, setTried] = useState(false);

  const mutation = useMutation({
    mutationFn: () =>
      sendMessage(
        threadId ? { threadId, text: text.trim() } : { topic, text: text.trim() },
        keyFor({ threadId, topic, text: text.trim() }),
      ),
    onSuccess: () => {
      release();
      setText('');
      setTried(false);
      announce(t('msg.sent'));
      void client.invalidateQueries({ queryKey: portalKeys.all });
    },
  });
  const empty = text.trim() === '';

  return (
    <form
      noValidate
      className={styles.stack}
      aria-label={threadId ? t('msg.reply') : t('msg.newTitle')}
      onSubmit={(event) => {
        event.preventDefault();
      }}
    >
      {threadId ? null : (
        <Select
          label={t('msg.topicLabel')}
          value={topic}
          onChange={(value) => {
            if (value) setTopic(value as MessageTopic);
          }}
          options={topics.map((x) => ({ id: x, label: t(`msg.topic.${x}`) }))}
        />
      )}
      <TextField
        label={threadId ? t('msg.replyLabel') : t('msg.textLabel')}
        multiline
        minRows={threadId ? 2 : 4}
        maxLength={maxLength}
        value={text}
        onChange={setText}
        errorMessage={tried && empty ? t('msg.textRequired') : undefined}
      />
      {mutation.isError ? <PortalProblem error={mutation.error} title={t('msg.failed')} /> : null}
      <div className={styles.actions}>
        <Button
          variant="primary"
          isLoading={mutation.isPending}
          onPress={() => {
            setTried(true);
            if (!empty) mutation.mutate();
          }}
        >
          {threadId ? t('msg.sendReply') : t('msg.send')}
        </Button>
      </div>
    </form>
  );
}

function Thread({ thread }: { thread: MessageThread }) {
  const { t } = useTranslation('portal');
  const fmt = usePortalFormat();
  return (
    <PortalSection
      title={t(`msg.topic.${thread.topic}`)}
      family={thread.awaitingStaff ? 'warning' : 'info'}
    >
      <div className={rows.pillRow}>
        <StatusPill
          semantic={thread.awaitingStaff ? 'pending-approval' : 'success'}
          text={thread.awaitingStaff ? t('msg.awaiting') : t('msg.answered')}
        />
      </div>
      <ol className={rows.thread}>
        {thread.messages.map((m) => (
          <li key={m.messageId} className={rows.bubble} data-from={m.direction}>
            <span className={rows.bubbleMeta}>
              {m.direction === 'CUSTOMER' ? t('msg.you') : t('msg.team')} · {fmt.dateTime(m.sentAt)}
            </span>
            <p className={rows.bubbleText}>{m.text}</p>
          </li>
        ))}
      </ol>
      <Compose threadId={thread.threadId} />
    </PortalSection>
  );
}

/** `/portal/messages`: the conversation threads with the insurer and the way to start a new one. */
export function MessagesPage() {
  const { t } = useTranslation('portal');
  usePortalTitle(t('nav.messages'));
  const query = useQuery({ queryKey: portalKeys.messages, queryFn: listMessages });

  return (
    <PortalPage>
      <PortalHero overline={t('msg.overline')} title={t('msg.title')} lead={t('msg.lead')} />
      <QueryView query={query} errorTitle={t('msg.loadFailed')}>
        {(page) => (
          <>
            {page.items.length === 0 ? (
              <EmptyState
                kind="first-use"
                headingLevel={2}
                headline={t('msg.empty')}
                description={t('msg.emptyBody')}
              />
            ) : (
              page.items.map((item) => <Thread key={item.thread.threadId} thread={item.thread} />)
            )}
            <PortalSection title={t('msg.newTitle')} family="brand">
              <Compose />
            </PortalSection>
          </>
        )}
      </QueryView>
    </PortalPage>
  );
}
