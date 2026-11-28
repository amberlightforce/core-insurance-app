import { parseDate } from '@internationalized/date';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate, useParams } from 'react-router';

import { useIdempotencyKey } from '../../../api/idempotency';
import type { ConfigChangeItemInput, ConfigChangeRequestView } from '../../../api/types';
import {
  Banner,
  Button,
  Combobox,
  DatePicker,
  Select,
  TextField,
  announce,
} from '../../../design-system';
import { LinkButton } from '../../staff/LinkButton';
import { PageHeader, Section } from '../../staff/PageHeader';
import { QueryView } from '../../staff/QueryView';
import styles from '../../staff/staff.module.css';
import { packErrorText } from '../packs/errors';
import { useConfigCaller } from './access';
import { useConfigChange, useSaveConfigChange } from './api';
import cfg from './Config.module.css';
import { configKeys, legalStatuses } from './keys';

const blankItem = (): ConfigChangeItemInput => ({
  key: '',
  value: '',
  legalStatus: 'Unverified',
  sourceRef: '',
});

const keyOptions = configKeys.map((k) => ({ id: k, label: k }));

function toInput(request: ConfigChangeRequestView): ConfigChangeItemInput[] {
  return request.items.map((i) => ({
    key: i.key,
    value: i.value,
    legalStatus: i.legalStatus as ConfigChangeItemInput['legalStatus'],
    sourceRef: i.sourceRef,
    motorPath: i.motorPath,
    validFrom: i.validFrom,
    validTo: i.validTo,
    note: i.note,
  }));
}

const dateOf = (value: string | null | undefined) => {
  try {
    return value ? parseDate(value) : null;
  } catch {
    return null;
  }
};

const complete = (i: ConfigChangeItemInput) =>
  i.key.trim() !== '' && i.value.trim() !== '' && i.sourceRef.trim() !== '';

function ConfigForm({ existing }: { existing: ConfigChangeRequestView | undefined }) {
  const { t } = useTranslation('market');
  const navigate = useNavigate();
  const { keyFor, release } = useIdempotencyKey();
  const mutation = useSaveConfigChange(existing?.id);
  const [reason, setReason] = useState(existing?.reason ?? '');
  const [items, setItems] = useState<ConfigChangeItemInput[]>(
    existing ? toInput(existing) : [blankItem()],
  );
  const [tried, setTried] = useState(false);

  const patch = (index: number, change: Partial<ConfigChangeItemInput>) => {
    setItems((all) => all.map((item, i) => (i === index ? { ...item, ...change } : item)));
  };
  const valid = reason.trim() !== '' && items.length > 0 && items.every(complete);

  const save = () => {
    setTried(true);
    if (!valid) return;
    const draft = { reason: reason.trim(), items: items.map((i) => ({ ...i, key: i.key.trim() })) };
    mutation.mutate(
      { draft, key: keyFor(draft) },
      {
        onSuccess: (response) => {
          release();
          announce(t('config.form.saved'));
          void navigate(`/admin/config/${response.request.id}`);
        },
      },
    );
  };

  return (
    <div className={styles.stack}>
      <PageHeader
        overline={t('config.overline')}
        title={existing ? t('config.form.editTitle') : t('config.form.newTitle')}
        {...(existing ? { recordId: existing.id.slice(-8) } : {})}
        actions={
          <LinkButton
            variant="secondary"
            to={existing ? `/admin/config/${existing.id}` : '/admin/config'}
          >
            {t('config.form.cancel')}
          </LinkButton>
        }
      />
      <form
        noValidate
        className={cfg.form}
        aria-label={existing ? t('config.form.editTitle') : t('config.form.newTitle')}
        onSubmit={(e) => {
          e.preventDefault();
        }}
      >
        <Section title={t('config.form.reasonSection')} family="info">
          <TextField
            label={t('config.form.reason')}
            multiline
            maxLength={256}
            showCount
            isRequired
            helperText={t('config.form.reasonHelp')}
            value={reason}
            onChange={setReason}
            errorMessage={tried && reason.trim() === '' ? t('config.form.required') : undefined}
          />
        </Section>
        <Section title={t('config.form.itemsSection')} family="brand" count={items.length}>
          <ul className={cfg.items}>
            {items.map((item, index) => {
              const label = t('config.form.itemN', { n: index + 1 });
              const miss = (v: string) =>
                tried && v.trim() === '' ? t('config.form.required') : undefined;
              return (
                <li key={index} className={cfg.item} aria-label={label}>
                  <div className={cfg.itemHead}>
                    <span className={cfg.key}>{label}</span>
                    {items.length > 1 ? (
                      <Button
                        variant="ghost"
                        size="sm"
                        onPress={() => {
                          setItems((all) => all.filter((_, i) => i !== index));
                        }}
                      >
                        {t('config.form.remove', { n: index + 1 })}
                      </Button>
                    ) : null}
                  </div>
                  <div className={cfg.fieldGrid}>
                    <Combobox
                      label={t('config.form.key')}
                      items={keyOptions}
                      inputValue={item.key}
                      onInputChange={(v) => {
                        patch(index, { key: v });
                      }}
                      isRequired
                      description={t('config.form.keyHelp')}
                      {...(tried && item.key.trim() === ''
                        ? { errorMessage: t('config.form.required') }
                        : {})}
                    />
                    <TextField
                      label={t('config.form.value')}
                      isRequired
                      value={item.value}
                      onChange={(v) => {
                        patch(index, { value: v });
                      }}
                      errorMessage={miss(item.value)}
                    />
                    <Select
                      label={t('config.form.legalStatus')}
                      isRequired
                      options={legalStatuses.map((s) => ({ id: s, label: t(`config.legal.${s}`) }))}
                      value={item.legalStatus}
                      onChange={(v) => {
                        if (v)
                          patch(index, { legalStatus: v as ConfigChangeItemInput['legalStatus'] });
                      }}
                    />
                    <TextField
                      label={t('config.form.source')}
                      isRequired
                      helperText={t('config.form.sourceHelp')}
                      value={item.sourceRef}
                      onChange={(v) => {
                        patch(index, { sourceRef: v });
                      }}
                      errorMessage={miss(item.sourceRef)}
                    />
                    <DatePicker
                      label={t('config.form.validFrom')}
                      value={dateOf(item.validFrom)}
                      onChange={(d) => {
                        patch(index, { validFrom: d ? d.toString() : null });
                      }}
                    />
                    <DatePicker
                      label={t('config.form.validTo')}
                      value={dateOf(item.validTo)}
                      onChange={(d) => {
                        patch(index, { validTo: d ? d.toString() : null });
                      }}
                    />
                    <div className={cfg.wide}>
                      <TextField
                        label={t('config.form.note')}
                        value={item.note ?? ''}
                        onChange={(v) => {
                          patch(index, { note: v === '' ? null : v });
                        }}
                      />
                    </div>
                  </div>
                </li>
              );
            })}
          </ul>
          <div className={styles.actions}>
            <Button
              variant="secondary"
              onPress={() => {
                setItems((all) => [...all, blankItem()]);
              }}
            >
              {t('config.form.add')}
            </Button>
          </div>
        </Section>
        {mutation.isError ? (
          <Banner variant="danger" live="alert" title={t('config.form.failed')}>
            {packErrorText(t, mutation.error)}
          </Banner>
        ) : null}
        <div className={styles.actions}>
          <Button variant="primary" isLoading={mutation.isPending} onPress={save}>
            {t('config.form.save')}
          </Button>
        </div>
      </form>
    </div>
  );
}

/** Create (`/admin/config/new`) or edit a DRAFT (`/admin/config/:id/edit`); only the maker may. */
export function ConfigFormPage() {
  const { t } = useTranslation('market');
  const { id } = useParams();
  const caller = useConfigCaller();
  const query = useConfigChange(id ?? '');
  if (!caller.canMake) {
    return (
      <div className={styles.page}>
        <Banner variant="info" live="none" title={t('config.form.notMakerTitle')}>
          {t('config.form.notMakerBody')}
        </Banner>
      </div>
    );
  }
  if (!id) {
    return (
      <div className={styles.page}>
        <ConfigForm existing={undefined} />
      </div>
    );
  }
  return (
    <div className={styles.page}>
      <QueryView query={query} notFoundMessage={t('config.detail.notFound')}>
        {(request) =>
          request.status === 'DRAFT' ? (
            <ConfigForm existing={request} />
          ) : (
            <Banner variant="info" live="none" title={t('config.form.notDraftTitle')}>
              {t('config.form.notDraftBody')}
            </Banner>
          )
        }
      </QueryView>
    </div>
  );
}
