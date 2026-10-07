import { useMemo, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

import type { PartySearchItem } from '../../api/types';
import {
  DataTable,
  EmptyState,
  SearchField,
  identifierColumn,
  textColumn,
  type DataColumn,
} from '../../design-system';
import { ProblemBanner } from '../staff/ProblemBanner';
import { useSearchParties } from './api';
import styles from '../staff/staff.module.css';

export interface PartySearchPanelProps {
  /** Enter or double-click on a result row. */
  onOpen: (party: PartySearchItem) => void;
  /** Shown in the empty-result state next to the hint (for example «Νέο πρόσωπο»). */
  emptyAction?: ReactNode;
  /** Label of the search box. */
  label?: string;
}

const minLength = 2;

/**
 * One search box posting to `pty.Party.searchByCriteria` (name, ΑΦΜ or party number all go in the body, never in
 * the URL: D-SLC-05), with the results in a DataTable. Identifiers in the results are masked by the API.
 */
export function PartySearchPanel({ onOpen, emptyAction, label }: PartySearchPanelProps) {
  const { t } = useTranslation('party');
  const search = useSearchParties();
  const [text, setText] = useState('');
  const [tooShort, setTooShort] = useState(false);

  const submit = (value: string) => {
    const term = value.trim();
    if (term.length < minLength) {
      setTooShort(true);
      return;
    }
    setTooShort(false);
    search.mutate({ criteria: term });
  };

  const columns = useMemo<DataColumn<PartySearchItem>[]>(
    () => [
      identifierColumn<PartySearchItem>(
        'partyNumber',
        t('search.columns.number'),
        (r) => r.partyNumber,
        {
          size: 140,
        },
      ),
      textColumn<PartySearchItem>('displayName', t('search.columns.name'), (r) => r.displayName, {
        size: 260,
      }),
      textColumn<PartySearchItem>('partyType', t('search.columns.type'), (r) =>
        r.partyType === 'PERSON' ? t('type.person') : t('type.organisation'),
      ),
      identifierColumn<PartySearchItem>('identifier', t('search.columns.identifier'), (r) =>
        r.maskedIdentifier
          ? `${r.maskedIdentifier.scheme} ${r.maskedIdentifier.maskedValue}`
          : null,
      ),
      textColumn<PartySearchItem>(
        'locality',
        t('search.columns.locality'),
        (r) => [r.primaryPostcode, r.primaryLocality].filter(Boolean).join(' ') || null,
      ),
      textColumn<PartySearchItem>('status', t('search.columns.status'), (r) => r.status),
    ],
    [t],
  );

  const items = search.data?.items ?? [];

  return (
    <div className={styles.stack}>
      <SearchField
        label={label ?? t('search.label')}
        {...(tooShort ? {} : { helperText: t('search.help') })}
        value={text}
        onChange={(value) => {
          setText(value);
          setTooShort(false);
        }}
        onSubmit={submit}
        onClear={() => {
          setText('');
          search.reset();
        }}
        placeholder={t('search.placeholder')}
      />
      {tooShort ? (
        <p role="alert" className={styles.unbalanced}>
          {t('search.tooShort', { count: minLength })}
        </p>
      ) : null}
      {search.isError ? (
        <ProblemBanner
          error={search.error}
          title={t('search.failed')}
          onRetry={() => {
            submit(text);
          }}
        />
      ) : null}
      {search.isIdle ? (
        <EmptyState
          kind="first-use"
          headingLevel={2}
          headline={t('search.idleTitle')}
          description={t('search.idleBody')}
        />
      ) : (
        <DataTable<PartySearchItem>
          searchable={false}
          aria-label={t('search.results')}
          columns={columns}
          data={items}
          getRowId={(r) => r.partyId}
          getRowLabel={(r) => r.displayName}
          isLoading={search.isPending}
          onOpen={onOpen}
          emptyState={
            search.isSuccess ? (
              <EmptyState
                kind="first-use"
                headingLevel={2}
                headline={t('search.noResultsTitle')}
                description={t('search.noResultsBody')}
                {...(emptyAction ? { action: emptyAction } : {})}
              />
            ) : undefined
          }
        />
      )}
    </div>
  );
}
