import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

import { useIdempotencyKey } from '../../api/idempotency';
import { Button, EmptyState, TextField } from '../../design-system';
import { PageHeader, Section } from '../staff/PageHeader';
import staff from '../staff/staff.module.css';
import { LinkButton } from '../staff/LinkButton';
import { createBatch, readRecent, remember } from './api';
import { parseItems } from './parseItems';
import styles from './Migration.module.css';
import { ProblemBanner } from './ProblemBanner';
import { RoutingPanel } from './RoutingPanel';
import { useMutation } from '@tanstack/react-query';

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Migration home: open a batch, create one from items, and look up / override routing. */
export function MigrationHomePage() {
  const { t } = useTranslation('migration');
  const navigate = useNavigate();
  const { keyFor, release } = useIdempotencyKey();
  const [openId, setOpenId] = useState('');
  const [openTried, setOpenTried] = useState(false);
  const [name, setName] = useState('');
  const [itemsText, setItemsText] = useState('');
  const [tried, setTried] = useState(false);
  const [recent] = useState(readRecent);

  const create = useMutation({
    mutationFn: ({ body, key }: { body: Parameters<typeof createBatch>[0]; key: string }) =>
      createBatch(body, key),
  });

  const items = parseItems(itemsText);
  const submit = () => {
    setTried(true);
    if (!name.trim() || !items) return;
    const body = { name: name.trim(), items };
    create.mutate(
      { body, key: keyFor(body) },
      {
        onSuccess: ({ batch }) => {
          release();
          remember({ batchId: batch.batchId, name: batch.name });
          void navigate(`/admin/migration/batches/${batch.batchId}`);
        },
      },
    );
  };

  return (
    <div className={staff.page}>
      <PageHeader
        variant="landing"
        overline={t('overline')}
        title={t('home.title')}
        description={t('home.lead')}
      />
      <Section title={t('home.openTitle')}>
        <form
          className={styles.inline}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            setOpenTried(true);
            if (uuid.test(openId.trim()))
              void navigate(`/admin/migration/batches/${openId.trim()}`);
          }}
        >
          <TextField
            label={t('home.batchId')}
            value={openId}
            onChange={setOpenId}
            errorMessage={
              openTried && !uuid.test(openId.trim()) ? t('home.batchIdInvalid') : undefined
            }
          />
          <Button type="submit" variant="primary">
            {t('home.open')}
          </Button>
        </form>
        {recent.length > 0 ? (
          <ul className={styles.timeline} aria-label={t('home.recent')}>
            {recent.map((b) => (
              <li key={b.batchId}>
                <LinkButton to={`/admin/migration/batches/${b.batchId}`}>{b.name}</LinkButton>
                <span className="ds-mono ds-caption">{b.batchId}</span>
              </li>
            ))}
          </ul>
        ) : (
          <EmptyState kind="first-use" headingLevel={3} headline={t('home.noRecent')} />
        )}
      </Section>
      <Section title={t('home.createTitle')}>
        <form
          className={styles.form}
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            submit();
          }}
        >
          <TextField
            label={t('home.name')}
            value={name}
            onChange={setName}
            errorMessage={tried && !name.trim() ? t('home.nameRequired') : undefined}
          />
          <TextField
            label={t('home.items')}
            multiline
            minRows={6}
            value={itemsText}
            onChange={setItemsText}
            helperText={t('home.itemsHelp')}
            errorMessage={tried && !items ? t('home.itemsInvalid') : undefined}
          />
          <div className={styles.actions}>
            <Button type="submit" variant="commit" isLoading={create.isPending}>
              {t('home.create')}
            </Button>
          </div>
          {create.isError ? <ProblemBanner error={create.error} /> : null}
        </form>
      </Section>
      <Section title={t('routing.title')}>
        <RoutingPanel />
      </Section>
    </div>
  );
}
